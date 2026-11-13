using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace SaruPdf.Updates;

internal sealed record UpdateFile(string Path, string Sha256);
internal sealed record UpdateManifest(string Product, string Version, UpdateFile[] Files);
internal sealed record PreparedUpdate(string JobDirectory, string StageDirectory, UpdateManifest Manifest);

internal static class UpdatePackage
{
    internal const string ManifestName = "update-manifest.json";
    internal static PreparedUpdate Prepare(string package)
    {
        string job = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(package))!, "apply-" + Guid.NewGuid().ToString("N"));
        string stage = Path.Combine(job, "staged"); Directory.CreateDirectory(stage);
        try
        {
            using var zip = ZipFile.OpenRead(package);
            if (zip.Entries.Count > 2000 || zip.Entries.Sum(e => e.Length) > 1024L * 1024 * 1024) throw new InvalidDataException("更新ZIPが大きすぎます。");
            var manifestEntry = zip.GetEntry(ManifestName) ?? throw new InvalidDataException("更新ファイル一覧がありません。");
            if (manifestEntry.Length > 1024 * 1024) throw new InvalidDataException("更新ファイル一覧が大きすぎます。");
            UpdateManifest manifest;
            using (var stream = manifestEntry.Open()) manifest = JsonSerializer.Deserialize<UpdateManifest>(stream) ?? throw new InvalidDataException("更新ファイル一覧が不正です。");
            if (manifest.Product != "SaruPDF" || !System.Version.TryParse(manifest.Version, out _) || manifest.Files is not { Length: > 0 and < 2000 })
                throw new InvalidDataException("SaruPDFの更新パッケージではありません。");
            var entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (zip.Entries.Count != manifest.Files.Length + 1) throw new InvalidDataException("ZIPのファイル数が一覧と一致しません。");
            foreach (var file in manifest.Files)
            {
                string target = SafePath(stage, file.Path);
                if (file.Path.Equals("Uninstall.exe", StringComparison.OrdinalIgnoreCase) || file.Path.Equals("SaruPDF.install.ini", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("インストール情報はZIPで置き換えません。");
                if (!entries.Add(file.Path.Replace('\\', '/')) || file.Path.Equals(ManifestName, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新ファイル一覧に重複があります。");
                var entry = zip.GetEntry(file.Path.Replace('\\', '/')) ?? throw new InvalidDataException("更新ファイルが不足しています。");
                if ((entry.ExternalAttributes & 0x400) != 0 || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new InvalidDataException("リンクを含むZIPは更新に利用できません。");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target);
                using var input = File.OpenRead(target);
                if (!Convert.ToHexString(SHA256.HashData(input)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("展開した更新ファイルのSHA-256が一致しません。");
            }
            if (!entries.Contains("SaruPDF.exe") || !entries.Contains("SaruPDF.dll") || zip.Entries.Any(e => e.FullName != ManifestName && !entries.Contains(e.FullName)))
                throw new InvalidDataException("更新ZIPのファイル構成が不正です。");
            File.WriteAllText(Path.Combine(stage, ManifestName), JsonSerializer.Serialize(manifest));
            return new(job, stage, manifest);
        }
        catch
        {
            // Only our fresh, validated extraction tree is removed.
            Directory.Delete(job, true); throw;
        }
    }
    internal static string SafePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains(':') || relative.Contains('\\') || relative.Split('/').Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ') || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) || Path.IsPathRooted(relative))
            throw new InvalidDataException("更新ファイルのパスが不正です。");
        string full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新先の外側を参照しています。");
        return full;
    }
}
