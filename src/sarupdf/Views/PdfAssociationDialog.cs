using Microsoft.Win32;
using SaruPdf.WindowsIntegration;

namespace SaruPdf.Views;

internal sealed class PdfAssociationDialog : Form
{
    private readonly Label status = new() { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(0, 10, 0, 0) };

    public PdfAssociationDialog()
    {
        Text = "SaruPDF — PDF の関連付け";
        ClientSize = new Size(620, 340);
        MinimumSize = new Size(580, 360);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.Controls.Add(new Label
        {
            Text = "SaruPDF を PDF を開くアプリとして登録できます。\n既定の PDF アプリにするには、Windows の設定画面で\n.pdf のアプリとして SaruPDF を選択してください。",
            AutoSize = true, Margin = new Padding(0, 0, 0, 12)
        }, 0, 0);
        var register = new Button { Text = "PDF を開くアプリの候補に登録", Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 4) };
        register.Click += (_, _) => Perform(() => { PdfAssociation.RegisterCurrent(); UpdateStatus(); });
        var settings = new Button { Text = "登録して Windows の既定アプリ設定を開く…", Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 4) };
        settings.Click += (_, _) => Perform(() =>
        {
            PdfAssociation.RegisterCurrent();
            PdfAssociation.OpenDefaultAppsSettings();
            status.Text = "Windows の画面で .pdf に SaruPDF を選択してください。\n候補に見つからない場合は設定画面を開き直してください。";
        });
        var close = new Button { Text = "閉じる", Anchor = AnchorStyles.Right, AutoSize = true, MinimumSize = new Size(96, 34), DialogResult = DialogResult.Cancel };
        CancelButton = close;
        layout.Controls.Add(register, 0, 1);
        layout.Controls.Add(settings, 0, 2);
        layout.Controls.Add(status, 0, 3);
        layout.Controls.Add(close, 0, 4);
        Controls.Add(layout);
        Perform(UpdateStatus);
    }

    private void UpdateStatus() => status.Text = PdfAssociation.IsRegistered(Registry.CurrentUser, PdfAssociation.CurrentExecutable)
        ? "この SaruPDF は PDF を開くアプリの候補に登録されています。\n現在の既定アプリは Windows の設定画面で確認できます。"
        : "この SaruPDF はまだ PDF の候補に登録されていません。";

    private void Perform(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { status.Text = $"関連付けの設定に失敗しました。\n{ex.Message}"; }
    }
}
