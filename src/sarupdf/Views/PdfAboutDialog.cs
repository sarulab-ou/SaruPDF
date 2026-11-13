using SaruPdf.Updates;

namespace SaruPdf.Views;

internal sealed class PdfAboutDialog : Form
{
    internal PdfAboutDialog()
    {
        Text = "SaruPDF バージョン情報";
        ClientSize = new Size(420, 190);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AppIcon.Apply(this);
        var information = new Label
        {
            Name = "VersionInformation", Dock = DockStyle.Fill, Padding = new Padding(24),
            Text = $"SaruPDF\nバージョン {GitHubUpdater.CurrentVersion}\n\nWindows用PDFビューアー・ページ操作アプリ"
        };
        var close = new Button { Name = "CloseAbout", Text = "閉じる", AutoSize = true, DialogResult = DialogResult.OK };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(8), FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.Add(close);
        Controls.Add(information); Controls.Add(buttons);
        AcceptButton = CancelButton = close;
    }
}
