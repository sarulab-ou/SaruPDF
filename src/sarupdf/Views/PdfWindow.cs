using SaruPdf.Documents;

namespace SaruPdf.Views;

internal sealed class PdfWindow : Form
{
    private readonly Panel content = new() { Dock = DockStyle.Fill };
    private readonly TextBox pathBox = new()
    {
        Dock = DockStyle.Bottom, ReadOnly = true, BorderStyle = BorderStyle.None,
        BackColor = Color.FromArgb(241, 243, 247), ForeColor = Color.FromArgb(65, 74, 90),
        TabStop = false, AccessibleName = "表示中の PDF のパス"
    };
    private readonly Panel errorPanel = new() { Dock = DockStyle.Top, Height = 108, Visible = false, Padding = new Padding(12), BackColor = Color.FromArgb(255, 241, 224) };
    private readonly TextBox errorText = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, BackColor = Color.FromArgb(255, 241, 224), AccessibleName = "エラー" };
    private PdfView? viewer;

    public event Action<string[]>? OpenRequested;

    public PdfWindow()
    {
        Text = "SaruPDF";
        ClientSize = new Size(1080, 800);
        MinimumSize = new Size(560, 420);
        StartPosition = FormStartPosition.WindowsDefaultLocation;
        BackColor = Color.White;
        AppIcon.Apply(this);
        var menu = new MenuStrip { Padding = new Padding(8, 5, 8, 5), BackColor = Color.FromArgb(247, 248, 251) };
        var file = new ToolStripMenuItem("ファイル(&F)");
        file.DropDownItems.Add(new ToolStripMenuItem("PDF を開く(&O)…", null, (_, _) => SelectFiles()) { ShortcutKeys = Keys.Control | Keys.O });
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(new ToolStripMenuItem("このウィンドウを閉じる(&W)", null, (_, _) => Close()) { ShortcutKeys = Keys.Control | Keys.W });
        menu.Items.Add(file);
        menu.Items.Add(new ToolStripMenuItem("PDF を開く…", null, (_, _) => SelectFiles()));
        var settings = new ToolStripMenuItem("設定(&S)");
        settings.DropDownItems.Add(new ToolStripMenuItem("PDF の関連付け(&A)…", null, (_, _) =>
        {
            using var dialog = new PdfAssociationDialog();
            dialog.ShowDialog(this);
        }));
        menu.Items.Add(settings);
        MainMenuStrip = menu;

        var dismiss = new Button { Text = "閉じる", Dock = DockStyle.Right, Width = 76, AccessibleName = "エラー表示を閉じる" };
        dismiss.Click += (_, _) => errorPanel.Visible = false;
        errorPanel.Controls.Add(errorText);
        errorPanel.Controls.Add(dismiss);

        var welcome = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(32) };
        welcome.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        welcome.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
        welcome.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        welcome.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        welcome.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        welcome.Controls.Add(new Label { Text = "SaruPDF", AutoSize = true, Font = new Font(Font.FontFamily, 30, FontStyle.Bold), ForeColor = Color.FromArgb(34, 53, 91), Anchor = AnchorStyles.None }, 0, 1);
        welcome.Controls.Add(new Label { Text = "PDF ごとに、ひとつのウィンドウ。", AutoSize = true, ForeColor = Color.FromArgb(94, 104, 122), Anchor = AnchorStyles.None }, 0, 2);
        var open = new Button { Text = "PDF を開く   Ctrl+O", AutoSize = true, Padding = new Padding(22, 10, 22, 10), Anchor = AnchorStyles.None, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(37, 77, 137), ForeColor = Color.White };
        open.Click += (_, _) => SelectFiles();
        welcome.Controls.Add(open, 0, 3);
        content.Controls.Add(welcome);
        Controls.Add(content);
        Controls.Add(errorPanel);
        Controls.Add(pathBox);
        Controls.Add(menu);
        Shown += async (_, _) => { if (viewer is not null) await viewer.LoadAsync(); };
        Deactivate += (_, _) => viewer?.CancelPageInput();
    }

    public void SetDocument(PdfDocument document)
    {
        if (viewer is not null) throw new InvalidOperationException("このウィンドウには既に PDF が割り当てられています。");
        Text = $"{document.FileName}（{document.Path}）";
        pathBox.Text = document.Path;
        foreach (Control child in content.Controls) child.Dispose();
        content.Controls.Clear();
        viewer = new PdfView(document) { Dock = DockStyle.Fill };
        viewer.OpenRequested += path => OpenRequested?.Invoke([path]);
        viewer.OpenDialogRequested += SelectFiles;
        viewer.CloseRequested += Close;
        content.Controls.Add(viewer);
        if (Visible) _ = viewer.LoadAsync();
    }

    public void ShowError(string message)
    {
        errorText.Text = message.ReplaceLineEndings(Environment.NewLine);
        errorPanel.Visible = true;
    }

    private void SelectFiles()
    {
        viewer?.CancelPageInput();
        using var dialog = new OpenFileDialog
        {
            Title = "PDF を開く", Filter = "PDF ファイル (*.pdf)|*.pdf",
            Multiselect = true, CheckFileExists = true, RestoreDirectory = true
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) OpenRequested?.Invoke(dialog.FileNames);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (viewer?.TryHandlePageKey(keyData) == true) return true;
        if (keyData == (Keys.Control | Keys.O)) { SelectFiles(); return true; }
        if (keyData == (Keys.Control | Keys.W)) { Close(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
