using SaruPdf.Documents;

namespace SaruPdf.Views;

internal sealed class PdfEditor : UserControl
{
    private readonly PdfPageClipboard clipboard;
    private bool loaded;
    internal PdfEditSession Session { get; }
    internal PdfPageCanvas Viewer { get; } = new();
    internal int SelectedIndex => Viewer.SelectedPages.FirstOrDefault(-1);
    internal bool CanCut => !Session.HasPendingCut && Viewer.SelectedPages.Count > 0;
    internal bool CanCopy => CanCut;
    internal bool CanPaste => Session.HasPendingCut || clipboard.Current is not null;
    internal event Action? StateChanged;
    internal event Action<Exception>? Failed;
    internal event Action<int>? ImportRequested;

    internal PdfEditor(PdfEditSession session, PdfPageClipboard? clipboard = null)
    {
        this.clipboard = clipboard ?? new PdfPageClipboard();
        Session = session;
        Dock = DockStyle.Fill;
        Controls.Add(Viewer);
        Viewer.SelectionChanged += UpdateState;
        Viewer.CutRequested += Cut;
        Viewer.CopyRequested += Copy;
        Viewer.PasteRequested += PasteAt;
        Viewer.ImportRequested += index => ImportRequested?.Invoke(index);
        Viewer.UndoRequested += Undo;
        Viewer.RedoRequested += Redo;
        Viewer.Failed += ex => Failed?.Invoke(ex);
        this.clipboard.Changed += UpdateState;
        RefreshPages(0);
    }
    internal async Task LoadAsync()
    {
        try { loaded = true; await Viewer.LoadSourcesAsync(Session.RenderPages()); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { if (!IsDisposed) Failed?.Invoke(ex); }
    }
    internal void Cut()
    {
        if (!CanCut) return;
        int first = Viewer.SelectedPages[0];
        Execute(() => { Session.Cut(Viewer.SelectedPages); RefreshPages(Math.Min(first, Session.PageCount)); });
    }
    internal void Paste() => PasteAt(Viewer.InsertionIndex);
    internal void Copy()
    {
        if (!CanCopy) return;
        Execute(() => clipboard.Set(Session.Copy(Viewer.SelectedPages)));
    }
    private void PasteAt(int index)
    {
        if (!CanPaste) return;
        int count = Session.HasPendingCut ? Session.CutPages.Count : clipboard.Current!.PageCount;
        Execute(() =>
        {
            if (Session.HasPendingCut) Session.Paste(index);
            else Session.PasteCopy(index, clipboard.Current!);
            RefreshPages(index, count);
        });
    }
    internal void Undo() => Execute(() => { if (Session.Undo()) RefreshPages(Math.Clamp(SelectedIndex, 0, Session.PageCount)); });
    internal void ImportFiles(int index, IEnumerable<string> paths) => Execute(() =>
    {
        int count = Session.ImportFiles(index, paths);
        RefreshPages(index, count);
    });
    internal void Redo() => Execute(() => { if (Session.Redo()) RefreshPages(Math.Clamp(SelectedIndex, 0, Session.PageCount)); });
    private void Execute(Action action)
    {
        try { UseWaitCursor = true; action(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Failed?.Invoke(ex); }
        finally { UseWaitCursor = false; }
    }
    internal void SelectPage(int page) => Viewer.SelectPage(page);
    internal void SelectEnd() => Viewer.SelectEnd();
    private void RefreshPages(int selection, int count = 1)
    {
        Viewer.SetOrder(Session.Pages, selection, count);
        if (loaded) _ = LoadAsync();
        UpdateState();
    }
    internal void UpdateState()
    {
        Viewer.HasPendingCut = Session.HasPendingCut;
        Viewer.CanPastePages = CanPaste;
        Viewer.PasteCount = Session.HasPendingCut ? Session.CutPages.Count : clipboard.Current?.PageCount ?? 0;
        Viewer.Invalidate(); StateChanged?.Invoke();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { clipboard.Changed -= UpdateState; Viewer.Dispose(); Session.Dispose(); }
        base.Dispose(disposing);
    }
}
