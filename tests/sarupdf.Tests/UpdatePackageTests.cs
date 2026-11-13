using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SaruPdf.Updates;

namespace SaruPdf.Tests;

internal static class UpdatePackageTests
{
    private static string Fixture(string root, string? badPath = null, bool badHash = false)
    {
        string folder = Path.GetFullPath(Path.Combine(root, "zip-update-" + Guid.NewGuid().ToString("N"))); Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "update.zip");
        var files = new Dictionary<string, byte[]>
        {
            [badPath ?? "SaruPDF.exe"] = File.ReadAllBytes(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe")),
            ["SaruPDF.dll"] = Encoding.UTF8.GetBytes("new application payload")
        };
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var file in files) { using var output = zip.CreateEntry(file.Key).Open(); output.Write(file.Value); }
        var manifest = new UpdateManifest("SaruPDF", "9.0.0", files.Select(f => new UpdateFile(f.Key, badHash ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(f.Value)))).ToArray());
        using var writer = new StreamWriter(zip.CreateEntry(UpdatePackage.ManifestName).Open()); writer.Write(JsonSerializer.Serialize(manifest));
        return path;
    }
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases(string root)
    {
        yield return ("ZIP update rejects traversal and tampered file contents", () =>
        {
            foreach (string path in new[] { "../outside.exe", "/absolute.exe", "C:/absolute.exe", "folder/../outside.exe", "folder\\escape.exe" })
            { Throws(() => UpdatePackage.Prepare(Fixture(root, path))); }
            Throws(() => UpdatePackage.Prepare(Fixture(root, badHash: true)));
            return Task.CompletedTask;
        });
        yield return ("ZIP updater replaces files and restarts without an installer", () => ApplyAsync(root, false));
        yield return ("ZIP updater rolls back replaced files when a later file is locked", () => ApplyAsync(root, true));
        yield return ("ZIP helper revalidates staged files before changing the application", async () =>
        {
            var update = UpdatePackage.Prepare(Fixture(root));
            string target = Target(root); byte[] original = File.ReadAllBytes(Path.Combine(target, "SaruPDF.exe"));
            File.WriteAllText(Path.Combine(update.StageDirectory, "SaruPDF.dll"), "tampered");
            using var worker = Worker();
            using var helper = Process.Start(UpdateLauncher.StartInfo(update, target, worker.Id, showErrors: false))!;
            await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Check(helper.ExitCode != 0 && File.ReadAllBytes(Path.Combine(target, "SaruPDF.exe")).SequenceEqual(original));
            await worker.WaitForExitAsync();
        });
    }
    private static string Target(string root)
    {
        string target = Path.GetFullPath(Path.Combine(root, "更新 it's & app-" + Guid.NewGuid().ToString("N"))); Directory.CreateDirectory(target);
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), Path.Combine(target, "SaruPDF.exe"));
        File.WriteAllText(Path.Combine(target, "SaruPDF.dll"), "old payload");
        File.WriteAllText(Path.Combine(target, "my-document.pdf"), "preserve user data"); return target;
    }
    private static Process Worker()
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe")) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-Command"); start.ArgumentList.Add("Start-Sleep -Seconds 3");
        return Process.Start(start)!;
    }
    private static async Task ApplyAsync(string root, bool locked)
    {
        var update = UpdatePackage.Prepare(Fixture(root)); string target = Target(root);
        byte[] oldExe = File.ReadAllBytes(Path.Combine(target, "SaruPDF.exe"));
        // Make the old executable distinguishable without breaking its PE image.
        using (var append = new FileStream(Path.Combine(target, "SaruPDF.exe"), FileMode.Append)) append.WriteByte(0);
        oldExe = File.ReadAllBytes(Path.Combine(target, "SaruPDF.exe"));
        string marker = Path.Combine(update.JobDirectory, "restarted.txt");
        using var held = locked ? new FileStream(Path.Combine(target, "SaruPDF.dll"), FileMode.Open, FileAccess.Read, FileShare.None) : null;
        using var worker = Worker();
        var start = UpdateLauncher.StartInfo(update, target, worker.Id, ["/c", "echo restarted > " + marker], showErrors: false);
        Check(start.CreateNoWindow && !start.UseShellExecute);
        using var helper = Process.Start(start)!;
        await Task.Delay(150); Check(File.ReadAllBytes(Path.Combine(target, "SaruPDF.exe")).SequenceEqual(oldExe));
        await worker.WaitForExitAsync();
        await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25));
        string result = File.ReadAllText(Path.Combine(update.JobDirectory, "result.json"));
        using var json = JsonDocument.Parse(result.TrimStart('\uFEFF'));
        Check(json.RootElement.GetProperty("Success").GetBoolean() == !locked);
        if (locked)
            Check(helper.ExitCode != 0 && json.RootElement.GetProperty("ChangedFiles").GetInt32() == 2 && json.RootElement.GetProperty("RollbackSucceeded").GetBoolean() && File.ReadAllBytes(Path.Combine(target, "SaruPDF.exe")).SequenceEqual(oldExe));
        else
        {
            Check(helper.ExitCode == 0 && File.ReadAllText(Path.Combine(target, "SaruPDF.dll")) == "new application payload");
            for (int i = 0; i < 50 && !File.Exists(marker); i++) await Task.Delay(100);
            Check(File.Exists(marker));
        }
        Check(File.ReadAllText(Path.Combine(target, "my-document.pdf")) == "preserve user data");
    }
    private static void Check(bool condition) { if (!condition) throw new Exception("ZIP update assertion failed."); }
    private static void Throws(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Expected invalid ZIP rejection"); }
}
