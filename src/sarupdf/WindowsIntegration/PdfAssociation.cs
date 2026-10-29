using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SaruPdf.WindowsIntegration;

internal static class PdfAssociation
{
    internal const string ProgId = "SaruPDF.Pdf";
    private const string Classes = @"Software\Classes";
    private const string Capabilities = @"Software\SaruPDF\Capabilities";
    private const string Applications = @"Software\RegisteredApplications";
    private const string AppPath = @"Software\Microsoft\Windows\CurrentVersion\App Paths\SaruPDF.exe";

    internal static string CurrentExecutable => Environment.ProcessPath
        ?? throw new InvalidOperationException("SaruPDF.exe の場所を確認できません。");

    internal static string OpenCommand(string executable)
    {
        if (!Path.IsPathFullyQualified(executable) || executable.IndexOfAny(['"', '\r', '\n', '\0']) >= 0 ||
            !string.Equals(Path.GetFileName(executable), "SaruPDF.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("SaruPDF.exe の絶対パスを指定してください。", nameof(executable));
        return $"\"{Path.GetFullPath(executable)}\" \"%1\"";
    }

    // RegistryKey injection allows testing against an isolated subtree, never the real associations.
    internal static void Register(RegistryKey userRoot, string executable)
    {
        string command = OpenCommand(executable);
        string path = Path.GetFullPath(executable);
        Set(userRoot, $@"{Classes}\{ProgId}", "", "PDF document (SaruPDF)");
        Set(userRoot, $@"{Classes}\{ProgId}", "FriendlyTypeName", "PDF document (SaruPDF)");
        Set(userRoot, $@"{Classes}\{ProgId}\DefaultIcon", "", $"\"{path}\",0");
        Set(userRoot, $@"{Classes}\{ProgId}\shell\open\command", "", command);
        Set(userRoot, $@"{Classes}\.pdf\OpenWithProgids", ProgId, "");
        Set(userRoot, $@"{Classes}\Applications\SaruPDF.exe", "FriendlyAppName", "SaruPDF");
        Set(userRoot, $@"{Classes}\Applications\SaruPDF.exe\SupportedTypes", ".pdf", "");
        Set(userRoot, $@"{Classes}\Applications\SaruPDF.exe\shell\open\command", "", command);
        Set(userRoot, Capabilities, "ApplicationName", "SaruPDF");
        Set(userRoot, Capabilities, "ApplicationDescription", "PDF ごとに独立したウィンドウで表示する PDF Viewer");
        Set(userRoot, Capabilities, "ApplicationIcon", $"\"{path}\",0");
        Set(userRoot, Capabilities, "OwnerExecutable", path);
        Set(userRoot, $@"{Capabilities}\FileAssociations", ".pdf", ProgId);
        Set(userRoot, Applications, "SaruPDF", Capabilities);
        Set(userRoot, AppPath, "", path);
        // Deliberately no .pdf default-value / Explorer FileExts UserChoice writes.
    }

    internal static bool IsRegistered(RegistryKey userRoot, string executable) =>
        Equal(Read(userRoot, $@"{Classes}\{ProgId}\shell\open\command", ""), OpenCommand(executable));

    internal static void Unregister(RegistryKey userRoot, string executable)
    {
        string command = OpenCommand(executable);
        if (IsRegistered(userRoot, executable))
        {
            userRoot.DeleteSubKeyTree($@"{Classes}\{ProgId}", false);
            DeleteValue(userRoot, $@"{Classes}\.pdf\OpenWithProgids", ProgId);
        }
        string application = $@"{Classes}\Applications\SaruPDF.exe";
        if (Equal(Read(userRoot, $@"{application}\shell\open\command", ""), command))
            userRoot.DeleteSubKeyTree(application, false);
        if (Equal(Read(userRoot, Capabilities, "OwnerExecutable"), Path.GetFullPath(executable)))
        {
            if (Equal(Read(userRoot, Applications, "SaruPDF"), Capabilities))
                DeleteValue(userRoot, Applications, "SaruPDF");
            userRoot.DeleteSubKeyTree(Capabilities, false);
        }
        if (Equal(Read(userRoot, AppPath, ""), Path.GetFullPath(executable)))
            userRoot.DeleteSubKeyTree(AppPath, false);
    }

    internal static void RegisterCurrent()
    {
        Register(Registry.CurrentUser, CurrentExecutable);
        NotifyShell();
    }

    internal static void UnregisterCurrent()
    {
        Unregister(Registry.CurrentUser, CurrentExecutable);
        NotifyShell();
    }

    internal static string SettingsUri(int windowsBuild) => windowsBuild >= 22000
        ? "ms-settings:defaultapps?registeredAppUser=SaruPDF" : "ms-settings:defaultapps";

    internal static void OpenDefaultAppsSettings()
    {
        var uri = SettingsUri(Environment.OSVersion.Version.Build);
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) when (uri != "ms-settings:defaultapps")
        { Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true }); }
    }

    private static void Set(RegistryKey root, string path, string name, string value)
    {
        using var key = root.CreateSubKey(path, true);
        key.SetValue(name, value, RegistryValueKind.String);
    }

    private static string? Read(RegistryKey root, string path, string name)
    {
        using var key = root.OpenSubKey(path);
        return key?.GetValue(name) as string;
    }

    private static void DeleteValue(RegistryKey root, string path, string name)
    {
        using var key = root.OpenSubKey(path, true);
        key?.DeleteValue(name, false);
    }

    private static bool Equal(string? left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static void NotifyShell() => SHChangeNotify(0x08000000, 0, 0, 0); // SHCNE_ASSOCCHANGED

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, nint item1, nint item2);
}
