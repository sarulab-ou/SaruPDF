using System.Net;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SaruPdf.Updates;
using SaruPdf.Views;
using static SaruPdf.Tests.PdfEditingTests;

namespace SaruPdf.Tests;

internal static class UpdateTests
{
    private static readonly byte[] Installer = Encoding.UTF8.GetBytes("test installer bytes; never executed");
    private static readonly string Hash = Convert.ToHexString(SHA256.HashData(Installer)).ToLowerInvariant();
    private static string Metadata(string tag = "v9.0.0", bool prerelease = false, string? downloadHost = null)
    {
        string version = tag.TrimStart('v');
        return JsonSerializer.Serialize(new { tag_name = tag, draft = false, prerelease, assets = new[]
        {
            new { name = $"SaruPDF-{version}-Update-x64.zip", state = "uploaded", size = Installer.Length, digest = "sha256:" + Hash,
                browser_download_url = $"https://{downloadHost ?? "github.com"}/sarulab-ou/SaruPDF/releases/download/{tag}/SaruPDF-{version}-Update-x64.zip" },
            new { name = $"SaruPDF-{version}-Update-SHA256SUMS.txt", state = "uploaded", size = 100, digest = "",
                browser_download_url = $"https://github.com/sarulab-ou/SaruPDF/releases/download/{tag}/SaruPDF-{version}-Update-SHA256SUMS.txt" }
        } });
    }
    private sealed class Handler(string metadata, bool corrupt = false, bool fail = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = new HttpResponseMessage(fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) { RequestMessage = request };
            string path = request.RequestUri!.AbsolutePath;
            response.Content = path.EndsWith("latest", StringComparison.Ordinal) ? new StringContent(metadata) : path.EndsWith(".txt", StringComparison.Ordinal)
                ? new StringContent(Hash + "  SaruPDF-9.0.0-Update-x64.zip\n") : new ByteArrayContent(corrupt ? Enumerable.Repeat((byte)0, Installer.Length).ToArray() : Installer);
            return Task.FromResult(response);
        }
    }
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases(string root)
    {
        yield return ("update compares semantic versions and ignores equal or older releases", async () =>
        {
            using var updater = new GitHubUpdater(new Handler(Metadata("v0.10.0")));
            Check((await updater.CheckAsync(new Version(0, 9, 9), default))?.Version == new Version(0, 10, 0));
            Check(await updater.CheckAsync(new Version(0, 10, 0), default) is null);
            Check(await updater.CheckAsync(new Version(1, 0, 0), default) is null);
        });
        yield return ("update rejects prerelease versions and foreign installer URLs", async () =>
        {
            foreach (string data in new[] { Metadata(prerelease: true), Metadata(downloadHost: "example.com"), Metadata("v9.0.0-preview"), Metadata().Replace("-Update-x64.zip", "-portable.zip", StringComparison.Ordinal) })
            {
                using var updater = new GitHubUpdater(new Handler(data));
                await ThrowsAsync<InvalidDataException>(() => updater.CheckAsync(new Version(1, 0, 0), default));
            }
        });
        yield return ("update download verifies SHA-256 and keeps only the verified installer", async () =>
        {
            using var updater = new GitHubUpdater(new Handler(Metadata()));
            var release = (await updater.CheckAsync(new Version(1, 0, 0), default))!;
            string folder = Path.Combine(root, "update-good-" + Guid.NewGuid().ToString("N"));
            string path = await updater.DownloadAsync(release, folder, null, default);
            Check(File.ReadAllBytes(path).SequenceEqual(Installer) && Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Length == 1);
        });
        yield return ("update corrupt downloads are removed and never returned for execution", async () =>
        {
            using var updater = new GitHubUpdater(new Handler(Metadata(), corrupt: true));
            var release = (await updater.CheckAsync(new Version(1, 0, 0), default))!;
            string folder = Path.Combine(root, "update-bad-" + Guid.NewGuid().ToString("N"));
            await ThrowsAsync<InvalidDataException>(() => updater.DownloadAsync(release, folder, null, default));
            Check(!Directory.EnumerateFileSystemEntries(folder).Any());
            await ThrowsAsync<InvalidDataException>(() => updater.DownloadAsync(release with { Digest = "sha256:" + new string('0', 64) }, folder, null, default));
            await ThrowsAsync<InvalidDataException>(() => updater.DownloadAsync(release with { Size = release.Size + 1 }, folder, null, default));
        });
        yield return ("update handles HTTP failures and cancellation", async () =>
        {
            using var updater = new GitHubUpdater(new Handler(Metadata(), fail: true));
            await ThrowsAsync<HttpRequestException>(() => updater.CheckAsync(new Version(1, 0, 0), default));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => updater.CheckAsync(new Version(1, 0, 0), cancelled.Token));
        });
    }

    internal static Task VerifyUiAsync(string root) => OnSurfaceAsync(async host =>
    {
        using var window = new PdfWindow { TopLevel = false }; host.Controls.Add(window); window.Show();
        bool requested = false; window.UpdateRequested += () => requested = true;
        var help = window.MainMenuStrip!.Items.OfType<ToolStripMenuItem>().Single(i => i.Name == "HelpMenu");
        bool versionShown = false;
        using (var closeAbout = new System.Windows.Forms.Timer { Interval = 50 })
        {
            closeAbout.Tick += (_, _) =>
            {
                var about = Application.OpenForms.OfType<PdfAboutDialog>().FirstOrDefault(); if (about is null) return;
                var information = about.Controls.Find("VersionInformation", true).OfType<Label>().Single();
                versionShown = information.Text.Contains("バージョン " + GitHubUpdater.CurrentVersion, StringComparison.Ordinal);
                closeAbout.Stop(); about.Controls.Find("CloseAbout", true).OfType<Button>().Single().PerformClick();
            };
            closeAbout.Start();
            help.DropDownItems.OfType<ToolStripMenuItem>().Single(i => i.Name == "AboutApplication").PerformClick();
        }
        Check(versionShown);
        help.DropDownItems.OfType<ToolStripMenuItem>().Single(i => i.Name == "UpdateApplication").PerformClick(); Check(requested);
        using (var dialog = new PdfUpdateDialog(new GitHubUpdater(new Handler(Metadata())), new Version(1, 0, 0), Path.Combine(root, "update-ui")) { TopLevel = false })
        {
            host.Controls.Add(dialog); dialog.Show();
            var button = dialog.Controls.Find("DownloadUpdate", true).OfType<Button>().Single();
            await WaitUntilAsync(() => button.Enabled); button.PerformClick();
            await WaitUntilAsync(() => dialog.PackagePath is not null && dialog.IsDisposed);
            Check(File.ReadAllBytes(dialog.PackagePath!).SequenceEqual(Installer));
        }
        using var current = new PdfUpdateDialog(new GitHubUpdater(new Handler(Metadata())), new Version(9, 0, 0)) { TopLevel = false };
        host.Controls.Add(current); current.Show();
        var label = current.Controls.Find("UpdateStatus", true).OfType<Label>().Single();
        await WaitUntilAsync(() => label.Text.Contains("最新版を使用", StringComparison.Ordinal));
        Check(!current.Controls.Find("DownloadUpdate", true).OfType<Button>().Single().Enabled);
        current.Close();
        string source = Path.Combine(root, "update-unsaved.pdf"); SamplePdf.Write(source, "Update unsaved document");
        byte[] original = File.ReadAllBytes(source);
        window.SetDocument(SaruPdf.Documents.DocumentPath.Open(source)); window.ToggleEditMode();
        var editor = PdfCopyTests.Editor(window); await WaitUntilAsync(() => editor.Visible);
        editor.Rotate(90); Check(editor.Session.IsDirty);
        bool cancelled = false;
        using var timer = new System.Windows.Forms.Timer { Interval = 50 };
        timer.Tick += (_, _) => EnumThreadWindows(GetCurrentThreadId(), (handle, _) =>
        {
            var title = new StringBuilder(128); GetWindowText(handle, title, title.Capacity);
            if (title.ToString() != "PDFの変更") return true;
            cancelled = true; timer.Stop(); PostMessage(handle, 0x0111, 2, 0); return false;
        }, 0);
        timer.Start(); Check(!window.PrepareForUpdate());
        Check(cancelled && window.Visible && editor.Session.IsDirty && File.ReadAllBytes(source).SequenceEqual(original));
        editor.Undo(); host.Controls.Remove(window);
    });
    private delegate bool WindowCallback(nint handle, nint parameter);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumThreadWindows(uint thread, WindowCallback callback, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint handle, StringBuilder text, int count);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(nint handle, uint message, nint wParam, nint lParam);
    private static void Check(bool condition) { if (!condition) throw new Exception("Update assertion failed."); }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
}
