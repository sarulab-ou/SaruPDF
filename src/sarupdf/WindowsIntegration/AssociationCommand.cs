using SaruPdf.Views;

namespace SaruPdf.WindowsIntegration;

internal static class AssociationCommand
{
    internal static string? Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("--register-pdf" or "--unregister-pdf" or "--pdf-association")) return null;
        if (args.Length == 1 || (args.Length == 2 && args[1] == "--quiet" && args[0] != "--pdf-association")) return args[0];
        throw new ArgumentException("関連付けコマンドの引数が正しくありません。");
    }

    internal static int? Handle(string[] args)
    {
        try
        {
            switch (Parse(args))
            {
                case "--register-pdf": PdfAssociation.RegisterCurrent(); return 0;
                case "--unregister-pdf": PdfAssociation.UnregisterCurrent(); return 0;
                case "--pdf-association":
                    using (var dialog = new PdfAssociationDialog()) Application.Run(dialog);
                    return 0;
                default: return null;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or ArgumentException)
        {
            if (!args.Contains("--quiet"))
                MessageBox.Show($"PDF の関連付けを設定できませんでした。\n{ex.Message}", "SaruPDF", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
