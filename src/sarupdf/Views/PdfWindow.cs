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
    private readonly string? userDataFolder;
    private readonly PdfPageClipboard pageClipboard;
    private PdfDocument? document;
    private PdfEditor? editor;
    private readonly ToolStripMenuItem editMode = new("編集モード(&M)") { Name = "EditMode", Enabled = false };
    private readonly ToolStripMenuItem cutPage = new("ページを切り取り(&T)") { ShortcutKeys = Keys.Control | Keys.X, Enabled = false };
    private readonly ToolStripMenuItem copyPage = new("ページをコピー(&C)") { ShortcutKeys = Keys.Control | Keys.C, Enabled = false };
    private readonly ToolStripMenuItem pastePage = new("ページを貼り付け(&P)") { ShortcutKeys = Keys.Control | Keys.V, Enabled = false };
    private readonly ToolStripMenuItem undoEdit = new("元に戻す(&U)") { ShortcutKeys = Keys.Control | Keys.Z, Enabled = false };
    private readonly ToolStripMenuItem redoEdit = new("やり直し(&R)") { ShortcutKeys = Keys.Control | Keys.Y, Enabled = false };
    private readonly ToolStripMenuItem save = new("保存(&S)") { ShortcutKeys = Keys.Control | Keys.S, Enabled = false };
    private readonly ToolStripMenuItem saveAs = new("名前を付けて保存(&A)…") { ShortcutKeys = Keys.Control | Keys.Shift | Keys.S, Enabled = false };
    private PdfView? ActiveViewer => editor is null ? viewer : null;

    public event Action<string[]>? OpenRequested;
    public event Action<PdfDocument>? DocumentPathChanged;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<string, bool>? CanUseDocumentPath { get; set; }

    public PdfWindow(string? userDataFolder = null, PdfPageClipboard? pageClipboard = null)
    {
        this.userDataFolder = userDataFolder;
        this.pageClipboard = pageClipboard ?? new PdfPageClipboard();
        Text = "SaruPDF";
        ClientSize = new Size(1080, 800);
        MinimumSize = new Size(560, 420);
        StartPosition = FormStartPosition.WindowsDefaultLocation;
        BackColor = Color.White;
        AppIcon.Apply(this);
        var menu = new MenuStrip { Padding = new Padding(8, 5, 8, 5), BackColor = Color.FromArgb(247, 248, 251) };
        var file = new ToolStripMenuItem("ファイル(&F)");
        file.DropDownItems.Add(new ToolStripMenuItem("PDF を開く(&O)…", null, (_, _) => SelectFiles()) { ShortcutKeys = Keys.Control | Keys.O });
        file.DropDownItems.Add(save);
        file.DropDownItems.Add(saveAs);
        save.Click += (_, _) => SaveEdits(false);
        saveAs.Click += (_, _) => SaveEdits(true);
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(new ToolStripMenuItem("このウィンドウを閉じる(&W)", null, (_, _) => Close()) { ShortcutKeys = Keys.Control | Keys.W });
        menu.Items.Add(file);
        var edit = new ToolStripMenuItem("編集(&E)");
        edit.DropDownItems.AddRange([editMode, new ToolStripSeparator(), cutPage, copyPage, pastePage, new ToolStripSeparator(), undoEdit, redoEdit]);
        editMode.Click += (_, _) => ToggleEditMode();
        cutPage.Click += (_, _) => editor?.Cut();
        copyPage.Click += (_, _) => editor?.Copy();
        pastePage.Click += (_, _) => editor?.Paste();
        undoEdit.Click += (_, _) => editor?.Undo();
        redoEdit.Click += (_, _) => editor?.Redo();
        menu.Items.Add(edit);
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
        Deactivate += (_, _) => CancelPageInput();
    }

    public void SetDocument(PdfDocument document)
    {
        if (viewer is not null) throw new InvalidOperationException("このウィンドウには既に PDF が割り当てられています。");
        this.document = document;
        Text = $"{document.FileName}（{document.Path}）";
        pathBox.Text = document.Path;
        foreach (Control child in content.Controls) child.Dispose();
        content.Controls.Clear();
        viewer = new PdfView(document, userDataFolder) { Dock = DockStyle.Fill };
        viewer.OpenRequested += path => OpenRequested?.Invoke([path]);
        viewer.OpenDialogRequested += SelectFiles;
        viewer.CloseRequested += Close;
        viewer.CommandKeyRequested += TryHandleEditKey;
        content.Controls.Add(viewer);
        editMode.Enabled = true;
        if (Visible) _ = viewer.LoadAsync();
    }

    public void ShowError(string message)
    {
        errorText.Text = message.ReplaceLineEndings(Environment.NewLine);
        errorPanel.Visible = true;
    }

    private void SelectFiles()
    {
        CancelPageInput();
        using var dialog = new OpenFileDialog
        {
            Title = "PDF を開く", Filter = "PDF ファイル (*.pdf)|*.pdf",
            Multiselect = true, CheckFileExists = true, RestoreDirectory = true
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) OpenRequested?.Invoke(dialog.FileNames);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (TryHandleEditKey(keyData)) return true;
        if (editor?.Viewer.TryHandlePageKey(keyData) == true) return true;
        if (ActiveViewer?.TryHandlePageKey(keyData) == true) return true;
        if (keyData == (Keys.Control | Keys.O)) { SelectFiles(); return true; }
        if (keyData == (Keys.Control | Keys.W)) { Close(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    internal void ToggleEditMode()
    {
        if (document is null || viewer is null) return;
        if (editor is not null)
        {
            if (!ConfirmFinishEditing()) return;
            content.Controls.Remove(editor);
            editor.Dispose(); editor = null;
            viewer.Visible = true;
            viewer.NavigateDocument(document);
            UpdateEditMenus();
            return;
        }
        PdfEditSession? session = null;
        try
        {
            UseWaitCursor = true;
            viewer.CancelPageInput();
            session = new PdfEditSession(document.Path);
            editor = new PdfEditor(session, pageClipboard);
            editor.StateChanged += UpdateEditMenus;
            editor.Failed += ex => ShowError("ページを編集できませんでした。\n" + ex.Message);
            viewer.Visible = false;
            content.Controls.Add(editor); editor.BringToFront();
            UpdateEditMenus();
            _ = editor.LoadAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (editor is not null) { content.Controls.Remove(editor); editor.Dispose(); editor = null; }
            else session?.Dispose();
            viewer.Visible = true;
            UpdateEditMenus();
            ShowError("このPDFを編集モードで開けませんでした。\n" + ex.Message);
        }
        finally { UseWaitCursor = false; }
    }

    private void UpdateEditMenus()
    {
        editMode.Checked = editor is not null;
        cutPage.Enabled = editor?.CanCut == true;
        copyPage.Enabled = editor?.CanCopy == true;
        pastePage.Enabled = editor?.CanPaste == true;
        undoEdit.Enabled = editor?.Session.CanUndo == true;
        redoEdit.Enabled = editor?.Session.CanRedo == true;
        save.Enabled = editor is not null && !editor.Session.HasPendingCut && editor.Session.IsDirty;
        saveAs.Enabled = editor is not null && !editor.Session.HasPendingCut;
    }

    private bool TryHandleEditKey(Keys keyData)
    {
        if (editor is null) return false;
        Action? command = keyData switch
        {
            Keys.Control | Keys.X => () => editor?.Cut(),
            Keys.Control | Keys.C => () => editor?.Copy(),
            Keys.Control | Keys.V => () => editor?.Paste(),
            Keys.Control | Keys.Z => () => editor?.Undo(),
            Keys.Control | Keys.Y => () => editor?.Redo(),
            Keys.Control | Keys.A => () => editor?.Viewer.SelectAll(),
            Keys.Control | Keys.S => () => SaveEdits(false),
            Keys.Control | Keys.Shift | Keys.S => () => SaveEdits(true),
            _ => null
        };
        if (command is null) return false;
        CancelPageInput();
        // WebView2 keyboard callbacks must return before navigation or dialogs.
        BeginInvoke(() => { if (!IsDisposed) command(); });
        return true;
    }

    private void CancelPageInput()
    {
        editor?.Viewer.CancelPageInput();
        viewer?.CancelPageInput();
    }

    private bool SaveEdits(bool choosePath)
    {
        if (editor is null || document is null) return false;
        if (editor.Session.HasPendingCut)
        {
            ShowError("切り取り中のページを貼り付けるか、元に戻してから保存してください。");
            return false;
        }
        if (!choosePath && !editor.Session.IsDirty) return true;
        string path = document.Path;
        if (choosePath)
        {
            using var dialog = new SaveFileDialog
            {
                Title = "編集したPDFを保存", Filter = "PDF ファイル (*.pdf)|*.pdf", DefaultExt = "pdf",
                AddExtension = true, FileName = document.FileName, InitialDirectory = Path.GetDirectoryName(document.Path),
                OverwritePrompt = true
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return false;
            path = Path.GetFullPath(dialog.FileName);
        }
        if (CanUseDocumentPath?.Invoke(path) == false)
        {
            ShowError("保存先のPDFは別のウィンドウで開かれています。そのウィンドウを閉じるか、別の名前で保存してください。");
            return false;
        }
        try
        {
            UseWaitCursor = true;
            editor.Session.Save(path);
            document = DocumentPath.Open(path);
            pathBox.Text = document.Path;
            Text = $"{document.FileName}（{document.Path}）";
            DocumentPathChanged?.Invoke(document);
            editor.UpdateState();
            errorPanel.Visible = false;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { ShowError("PDFを保存できませんでした。編集内容は保持しています。\n" + ex.Message); return false; }
        finally { UseWaitCursor = false; }
    }

    private bool ConfirmFinishEditing()
    {
        if (editor?.Session.IsDirty != true) return true;
        var answer = MessageBox.Show(this, "ページの編集内容を保存しますか？\n「いいえ」を選ぶと、未保存の変更を破棄します。", "PDFの編集", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        return answer == DialogResult.No || (answer == DialogResult.Yes && SaveEdits(false));
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!ConfirmFinishEditing()) e.Cancel = true;
        base.OnFormClosing(e);
    }
}
