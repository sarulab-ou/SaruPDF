using SaruPdf.Documents;

namespace SaruPdf.Views;

internal sealed class PdfEditor : UserControl
{
    private readonly ListBox pageList = new() { Dock = DockStyle.Fill, IntegralHeight = false, AccessibleName = "編集するページと挿入先", Name = "PageList" };
    private readonly Label status = new() { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(4, 8, 4, 8), MaximumSize = new Size(250, 0) };
    private readonly Label empty = new() { Dock = DockStyle.Fill, Text = "ページを切り取り中です。\n左の「末尾」を選んで貼り付けてください。", TextAlign = ContentAlignment.MiddleCenter, Visible = false };
    private readonly Button cutButton;
    private readonly Button pasteButton;
    private readonly Button undoButton;
    private readonly Button redoButton;
    private PdfDocument? preview;
    private bool updating;

    internal PdfEditSession Session { get; }
    internal PdfView Viewer { get; }
    internal int SelectedIndex => pageList.SelectedIndex;
    internal bool CanCut => !Session.HasPendingCut && SelectedIndex >= 0 && SelectedIndex < Session.PageCount;
    internal bool CanPaste => Session.HasPendingCut && SelectedIndex >= 0;
    internal event Action? StateChanged;
    internal event Action<Exception>? Failed;
    internal event Action? SaveRequested;

    internal PdfEditor(PdfEditSession session, string? userDataFolder = null)
    {
        Session = session;
        Dock = DockStyle.Fill;
        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, Size = new Size(1000, 700), SplitterDistance = 272, Panel1MinSize = 240, Panel2MinSize = 180 };
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(8) };
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.Controls.Add(new Label { Text = "編集モード\nページを切り取り、挿入先を選んで貼り付け", AutoSize = true, Padding = new Padding(0, 0, 0, 8) }, 0, 0);
        left.Controls.Add(pageList, 0, 1);
        left.Controls.Add(status, 0, 2);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        Button AddButton(string name, string text, Action action)
        {
            var button = new Button { Name = name, Text = text, AutoSize = true, MinimumSize = new Size(110, 32) };
            button.Click += (_, _) => action(); buttons.Controls.Add(button); return button;
        }
        cutButton = AddButton("CutPage", "切り取り", Cut);
        pasteButton = AddButton("PastePage", "貼り付け", Paste);
        undoButton = AddButton("UndoEdit", "元に戻す", Undo);
        redoButton = AddButton("RedoEdit", "やり直し", Redo);
        AddButton("SaveEdit", "保存", () => SaveRequested?.Invoke());
        left.Controls.Add(buttons, 0, 3);
        split.Panel1.Controls.Add(left);
        Viewer = new PdfView(DocumentPath.Open(session.SourcePath), userDataFolder) { Dock = DockStyle.Fill };
        split.Panel2.Controls.Add(Viewer);
        split.Panel2.Controls.Add(empty);
        Controls.Add(split);
        pageList.SelectedIndexChanged += (_, _) =>
        {
            if (updating) return;
            ShowSelection(); UpdateState();
        };
        Viewer.PageJumpRequested += SelectPage;
        RefreshPages(0);
    }

    internal Task LoadAsync() => Viewer.LoadAsync();

    internal void Cut()
    {
        if (!CanCut) return;
        int index = SelectedIndex;
        Execute(() => { Session.Cut(index); RefreshPages(Math.Min(index, Session.PageCount)); });
    }
    internal void Paste()
    {
        if (!CanPaste) return;
        int index = SelectedIndex;
        Execute(() => { Session.Paste(index); RefreshPages(index); });
    }
    internal void Undo() => Execute(() => { if (Session.Undo()) RefreshPages(Math.Min(SelectedIndex, Session.PageCount)); });
    internal void Redo() => Execute(() => { if (Session.Redo()) RefreshPages(Math.Min(SelectedIndex, Session.PageCount)); });
    private void Execute(Action action)
    {
        try { UseWaitCursor = true; action(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Failed?.Invoke(ex); }
        finally { UseWaitCursor = false; }
    }

    internal void SelectPage(int page)
    {
        if (page < 1 || page > Session.PageCount) return;
        pageList.SelectedIndex = page - 1;
    }
    internal void SelectEnd() => pageList.SelectedIndex = Session.PageCount;

    private void RefreshPages(int selection)
    {
        updating = true;
        pageList.BeginUpdate();
        try
        {
            pageList.Items.Clear();
            for (int i = 0; i < Session.PageCount; i++)
                pageList.Items.Add($"{i + 1} ページ  （元の {Session.Pages[i] + 1} ページ）");
            pageList.Items.Add("末尾（ここに貼り付け）");
            pageList.SelectedIndex = Math.Clamp(selection, 0, Session.PageCount);
        }
        finally { pageList.EndUpdate(); updating = false; }
        string? path = Session.WritePreview();
        preview = path is null ? null : DocumentPath.Open(path);
        Viewer.Visible = preview is not null;
        empty.Visible = preview is null;
        if (preview is null) empty.BringToFront();
        ShowSelection(); UpdateState();
    }

    private void ShowSelection()
    {
        if (preview is not null)
            Viewer.NavigateDocument(preview, Math.Min(SelectedIndex + 1, Session.PageCount));
    }

    internal void UpdateState()
    {
        cutButton.Enabled = CanCut; pasteButton.Enabled = CanPaste;
        undoButton.Enabled = Session.CanUndo; redoButton.Enabled = Session.CanRedo;
        status.Text = Session.HasPendingCut
            ? $"元の {Session.CutPage + 1} ページを切り取り中。選んだページの前、または末尾に貼り付けます。保存する前に貼り付けてください。"
            : $"{Session.PageCount} ページ · {(Session.IsDirty ? "未保存の変更あり" : "保存済み")}\n貼り付けは選択ページの前に挿入します。";
        StateChanged?.Invoke();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { Viewer.Dispose(); Session.Dispose(); }
        base.Dispose(disposing);
    }
}
