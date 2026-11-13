using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

namespace SaruPdf.Updates;

internal static class UpdateLauncher
{
    internal static string InstallDirectory()
    {
        static bool Installed(string? path) => path is not null && File.Exists(Path.Combine(path, "SaruPDF.install.ini")) &&
            File.ReadAllText(Path.Combine(path, "SaruPDF.install.ini")).Contains("Product=SaruPDF-Local-Install-v1", StringComparison.Ordinal);
        if (Installed(AppContext.BaseDirectory)) return Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\SaruPDF");
        if (key?.GetValue("InstallDir") is string registered && Installed(registered)) return Path.GetFullPath(registered);
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "SaruPDF");
    }
    internal static ProcessStartInfo StartInfo(string installer, string directory, int processId)
    {
        static string Quote(string text) => "'" + text.Replace("'", "''") + "'";
        if (!Path.IsPathFullyQualified(installer) || !Path.IsPathFullyQualified(directory) || directory.IndexOfAny(['\r', '\n', '"']) >= 0)
            throw new ArgumentException("更新ファイルまたはインストール先のパスが不正です。");
        // Run outside the application payload so the installer can replace all
        // its binaries. Wait for every PDF window and WebView to be disposed.
        string script = $$"""
            $ErrorActionPreference = 'Stop'
            try {
                $app = Get-Process -Id {{processId}} -ErrorAction SilentlyContinue
                if ($null -ne $app -and -not $app.WaitForExit(120000)) { throw 'SaruPDFの終了を待機できませんでした。' }
                Start-Process -FilePath {{Quote(installer)}} -ArgumentList {{Quote("/NoAssociation /D=" + directory)}}
            } catch {
                Add-Type -AssemblyName System.Windows.Forms
                [System.Windows.Forms.MessageBox]::Show('更新を開始できませんでした。' + [Environment]::NewLine + $_.Exception.Message, 'SaruPDF 更新') | Out-Null
            }
            """;
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(installer)! };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        return start;
    }
    internal static void Start(string installer)
    {
        using var process = Process.Start(StartInfo(installer, InstallDirectory(), Environment.ProcessId)) ?? throw new IOException("更新用プロセスを起動できませんでした。");
    }
}
