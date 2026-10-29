using Microsoft.Win32;
using SaruPdf.WindowsIntegration;

namespace SaruPdf.Tests;

internal static class AssociationTests
{
    private const string Exe = @"C:\アプリ テスト #100% &\SaruPDF.exe";

    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("association command quotes Japanese and special-character paths", () =>
            Check(PdfAssociation.OpenCommand(Exe) == $"\"{Exe}\" \"%1\""));
        yield return ("association registration exposes open-with and capabilities", () => WithRegistry(root =>
        {
            PdfAssociation.Register(root, Exe);
            Check(PdfAssociation.IsRegistered(root, Exe));
            Check(Read(root, @"Software\Classes\.pdf\OpenWithProgids", PdfAssociation.ProgId) == "");
            Check(Read(root, @"Software\SaruPDF\Capabilities\FileAssociations", ".pdf") == PdfAssociation.ProgId);
            Check(Read(root, @"Software\RegisteredApplications", "SaruPDF") == @"Software\SaruPDF\Capabilities");
            Check(Read(root, @"Software\Classes\Applications\SaruPDF.exe\shell\open\command", "") == PdfAssociation.OpenCommand(Exe));
        }));
        yield return ("association changes preserve the existing PDF default and UserChoice", () => WithRegistry(root =>
        {
            using (var pdf = root.CreateSubKey(@"Software\Classes\.pdf")) pdf.SetValue("", "Existing.Pdf");
            using (var choice = root.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.pdf\UserChoice"))
            { choice.SetValue("ProgId", "Existing.Pdf"); choice.SetValue("Hash", "test-only-sentinel"); }
            PdfAssociation.Register(root, Exe);
            PdfAssociation.Unregister(root, Exe);
            Check(Read(root, @"Software\Classes\.pdf", "") == "Existing.Pdf");
            Check(Read(root, @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.pdf\UserChoice", "ProgId") == "Existing.Pdf");
            Check(Read(root, @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.pdf\UserChoice", "Hash") == "test-only-sentinel");
        }));
        yield return ("unregister removes only this application's registration", () => WithRegistry(root =>
        {
            PdfAssociation.Register(root, Exe);
            using (var other = root.CreateSubKey(@"Software\Classes\.pdf\OpenWithProgids")) other.SetValue("Other.Pdf", "");
            using (var other = root.CreateSubKey(@"Software\RegisteredApplications")) other.SetValue("OtherApp", @"Software\OtherApp\Capabilities");
            PdfAssociation.Unregister(root, Exe);
            Check(!PdfAssociation.IsRegistered(root, Exe));
            Check(Read(root, @"Software\Classes\.pdf\OpenWithProgids", "Other.Pdf") == "");
            Check(Read(root, @"Software\RegisteredApplications", "OtherApp") == @"Software\OtherApp\Capabilities");
            Check(Read(root, @"Software\RegisteredApplications", "SaruPDF") is null);
            Check(Read(root, @"Software\Microsoft\Windows\CurrentVersion\App Paths\SaruPDF.exe", "") is null);
        }));
        yield return ("old-copy uninstall cannot remove a newer copy's registration", () => WithRegistry(root =>
        {
            string newer = @"C:\Different copy\SaruPDF.exe";
            PdfAssociation.Register(root, Exe);
            PdfAssociation.Register(root, newer);
            PdfAssociation.Unregister(root, Exe);
            Check(PdfAssociation.IsRegistered(root, newer));
            Check(Read(root, @"Software\SaruPDF\Capabilities", "OwnerExecutable") == newer);
            Check(Read(root, @"Software\Classes\.pdf\OpenWithProgids", PdfAssociation.ProgId) == "");
        }));
        yield return ("register and unregister are idempotent", () => WithRegistry(root =>
        {
            PdfAssociation.Register(root, Exe);
            PdfAssociation.Register(root, Exe);
            PdfAssociation.Unregister(root, Exe);
            PdfAssociation.Unregister(root, Exe);
            Check(!PdfAssociation.IsRegistered(root, Exe));
        }));
        yield return ("association CLI parsing and Windows settings destinations", () =>
        {
            Check(AssociationCommand.Parse(["--register-pdf", "--quiet"]) == "--register-pdf");
            Check(AssociationCommand.Parse(["--unregister-pdf"]) == "--unregister-pdf");
            Check(AssociationCommand.Parse(["A.pdf"]) is null);
            bool rejected = false;
            try { AssociationCommand.Parse(["--register-pdf", "unexpected.pdf"]); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected);
            Check(PdfAssociation.SettingsUri(19045) == "ms-settings:defaultapps");
            Check(PdfAssociation.SettingsUri(26200) == "ms-settings:defaultapps?registeredAppUser=SaruPDF");
        });
    }

    private static void WithRegistry(Action<RegistryKey> run)
    {
        string isolated = @"Software\SaruPDF.Tests\" + Guid.NewGuid().ToString("N");
        try
        {
            using var root = Registry.CurrentUser.CreateSubKey(isolated, true);
            run(root);
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(isolated, false); }
    }

    private static string? Read(RegistryKey root, string path, string name)
    {
        using var key = root.OpenSubKey(path);
        return key?.GetValue(name) as string;
    }

    private static void Check(bool value) { if (!value) throw new Exception("Association assertion failed"); }
}
