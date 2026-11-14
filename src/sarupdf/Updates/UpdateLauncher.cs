using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace SaruPdf.Updates;

internal static class UpdateLauncher
{
    internal static ProcessStartInfo StartInfo(PreparedUpdate update, string directory, int processId, string[]? documents = null, bool showErrors = true, string? resumeSession = null)
    {
        string target = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        if (!Path.IsPathFullyQualified(directory) || target.Equals(Path.GetPathRoot(target)?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) || !File.Exists(Path.Combine(target, "SaruPDF.exe")))
            throw new ArgumentException("SaruPDFの更新先を確認できません。");
        string transaction = Path.Combine(Path.GetDirectoryName(target)!, ".SaruPDF-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(transaction);
        string request = Path.Combine(update.JobDirectory, "request.json");
        File.WriteAllText(request, JsonSerializer.Serialize(new
        {
            Target = target, Stage = update.StageDirectory, Transaction = transaction, ProcessId = processId,
            Version = update.Manifest.Version, Documents = documents ?? [], ShowErrors = showErrors,
            ResumeSession = resumeSession,
            Result = Path.Combine(update.JobDirectory, "result.json")
        }));
        using var resource = typeof(UpdateLauncher).Assembly.GetManifestResourceStream("SaruPdf.Updates.ApplyUpdate.ps1")!;
        using var reader = new StreamReader(resource);
        string script = "$RequestPath = '" + request.Replace("'", "''") + "'\n" + reader.ReadToEnd();
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = update.JobDirectory };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        return start;
    }
    internal static void Start(PreparedUpdate update, UpdateSession session)
    {
        string resume = Path.Combine(update.JobDirectory, "resume-session.json");
        UpdateSessionFile.Write(resume, session);
        using var process = Process.Start(StartInfo(update, AppContext.BaseDirectory, Environment.ProcessId,
            session.Windows.Select(w => w.Path).OfType<string>().ToArray(), resumeSession: resume)) ?? throw new IOException("更新用プロセスを起動できませんでした。");
    }
}
