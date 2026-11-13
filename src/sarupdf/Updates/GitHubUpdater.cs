using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SaruPdf.Updates;

internal sealed record UpdateRelease(Version Version, string InstallerName, Uri InstallerUrl, long Size, Uri ChecksumsUrl, string? Digest);

internal sealed class GitHubUpdater : IDisposable
{
    internal const string Repository = "sarulab-ou/SaruPDF";
    private const long MaximumInstallerSize = 256 * 1024 * 1024;
    private readonly HttpClient client;
    internal GitHubUpdater(HttpMessageHandler? handler = null)
    {
        client = handler is null ? new HttpClient() : new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SaruPDF-Updater/1.0");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }
    internal static Version CurrentVersion => new(typeof(GitHubUpdater).Assembly.GetName().Version!.ToString(3));

    internal async Task<UpdateRelease?> CheckAsync(Version current, CancellationToken token)
    {
        using var response = await client.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) throw new InvalidDataException("公開されたリリースが見つかりません。");
        response.EnsureSuccessStatusCode();
        byte[] metadata = await ReadLimitedAsync(response, 1024 * 1024, token);
        using var json = JsonDocument.Parse(metadata);
        var release = json.RootElement;
        string tag = release.GetProperty("tag_name").GetString() ?? "";
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean() ||
            !Regex.IsMatch(tag, @"^v\d+\.\d+\.\d+$") || !Version.TryParse(tag[1..], out var version))
            throw new InvalidDataException("正式版のリリース情報を確認できません。");
        if (version <= current) return null;
        string installer = $"SaruPDF-{version}-Setup-x64.exe", checksums = $"SaruPDF-{version}-SHA256SUMS.txt";
        JsonElement Asset(string name)
        {
            var assets = release.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString() == name && a.GetProperty("state").GetString() == "uploaded").ToArray();
            if (assets.Length != 1) throw new InvalidDataException("更新用の配布ファイルが揃っていません。");
            return assets[0];
        }
        Uri Url(JsonElement asset, string name)
        {
            string expected = $"https://github.com/{Repository}/releases/download/{tag}/{name}";
            if (asset.GetProperty("browser_download_url").GetString() != expected) throw new InvalidDataException("配布元のURLが一致しません。");
            return new(expected);
        }
        var setup = Asset(installer); var checksum = Asset(checksums);
        long size = setup.GetProperty("size").GetInt64();
        if (size <= 0 || size > MaximumInstallerSize) throw new InvalidDataException("更新ファイルのサイズが不正です。");
        string? digest = setup.TryGetProperty("digest", out var hash) && hash.ValueKind == JsonValueKind.String ? hash.GetString() : null;
        return new(version, installer, Url(setup, installer), size, Url(checksum, checksums), digest);
    }

    internal async Task<string> DownloadAsync(UpdateRelease release, string root, IProgress<int>? progress, CancellationToken token)
    {
        using var checksumResponse = await client.GetAsync(release.ChecksumsUrl, HttpCompletionOption.ResponseHeadersRead, token);
        checksumResponse.EnsureSuccessStatusCode(); EnsureHttps(checksumResponse);
        string manifest = System.Text.Encoding.UTF8.GetString(await ReadLimitedAsync(checksumResponse, 16384, token)).Trim();
        var match = Regex.Match(manifest, @"\A([a-fA-F0-9]{64})  " + Regex.Escape(release.InstallerName) + @"\z");
        if (!match.Success) throw new InvalidDataException("チェックサム一覧の形式が不正です。");
        string expected = match.Groups[1].Value;
        if (release.Digest is not null && !string.Equals(release.Digest, "sha256:" + expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("GitHubのチェックサムと配布ファイルの一覧が一致しません。");
        string folder = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        string destination = Path.Combine(folder, release.InstallerName), partial = destination + ".download";
        try
        {
            using var response = await client.GetAsync(release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode(); EnsureHttps(response);
            if (response.Content.Headers.ContentLength is { } length && length != release.Size) throw new InvalidDataException("ダウンロードサイズが一致しません。");
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                byte[] buffer = new byte[81920]; long total = 0; int count;
                while ((count = await input.ReadAsync(buffer, token)) > 0)
                {
                    total += count;
                    if (total > release.Size || total > MaximumInstallerSize) throw new InvalidDataException("ダウンロードサイズが上限を超えました。");
                    await output.WriteAsync(buffer.AsMemory(0, count), token); hash.AppendData(buffer, 0, count);
                    progress?.Report((int)(total * 100 / release.Size));
                }
                if (total != release.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("更新ファイルのSHA-256が一致しません。更新を中止しました。");
                await output.FlushAsync(token);
            }
            token.ThrowIfCancellationRequested(); File.Move(partial, destination); return destination;
        }
        catch
        {
            File.Delete(partial); File.Delete(destination); Directory.Delete(folder); throw;
        }
    }
    private static void EnsureHttps(HttpResponseMessage response)
    {
        if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException("HTTPSで配布ファイルを取得できませんでした。");
    }
    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, int limit, CancellationToken token)
    {
        await using var input = await response.Content.ReadAsStreamAsync(token); using var output = new MemoryStream();
        byte[] buffer = new byte[8192]; int count;
        while ((count = await input.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + count > limit) throw new InvalidDataException("リリース情報が大きすぎます。");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
    public void Dispose() => client.Dispose();
}
