using SaruPdf.Documents;

namespace SaruPdf.Views;

internal sealed class PdfEditor : UserControl
{
    internal PdfEditSession Session { get; }
    internal PdfPageCanvas Viewer { get; } = new();
    internal int SelectedIndex => Viewer.SelectedPages.FirstOrDefault(-1);
    internal bool CanCut => !Session.HasPendingCut && Viewer.SelectedPages.Count > 0;
    internal bool CanPaste => Session.HasPendingCut;
    internal event Action? StateChanged;
    internal event Action<Exception>? Failed;

    internal PdfEditor(PdfEditSession session)
    {
        Session = session;
        Dock = DockStyle.Fill;
        Controls.Add(Viewer);
        Viewer.SelectionChanged += UpdateState;
        Viewer.CutRequested += Cut;
        Viewer.PasteRequested += PasteAt;
        Viewer.UndoRequested += Undo;
        Viewer.RedoRequested += Redo;
        Viewer.Failed += ex => Failed?.Invoke(ex);
        RefreshPages(0);
    }
    internal async Task LoadAsync()
    {
        try { await Viewer.LoadAsync(Session.WriteOriginalPreview()); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { if (!IsDisposed) Failed?.Invoke(ex); }
    }
    internal void Cut()
    {
        if (!CanCut) return;
        int first = Viewer.SelectedPages[0];
        Execute(() => { Session.Cut(Viewer.SelectedPages); RefreshPages(Math.Min(first, Session.PageCount)); });
    }
    internal void Paste() => PasteAt(Viewer.InsertionIndex);
    private void PasteAt(int index)
    {
        if (!CanPaste) return;
        int count = Session.CutPages.Count;
        Execute(() => { Session.Paste(index); RefreshPages(index, count); });
    }
    internal void Undo() => Execute(() => { if (Session.Undo()) RefreshPages(Math.Clamp(SelectedIndex, 0, Session.PageCount)); });
    internal void Redo() => Execute(() => { if (Session.Redo()) RefreshPages(Math.Clamp(SelectedIndex, 0, Session.PageCount)); });
    private void Execute(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Failed?.Invoke(ex); }
    }
    internal void SelectPage(int page) => Viewer.SelectPage(page);
    internal void SelectEnd() => Viewer.SelectEnd();
    private void RefreshPages(int selection, int count = 1)
    {
        Viewer.SetOrder(Session.Pages, selection, count);
        UpdateState();
    }
    internal void UpdateState()
    {
        Viewer.HasCutPages = Session.HasPendingCut;
        Viewer.CutCount = Session.CutPages.Count;
        Viewer.Invalidate(); StateChanged?.Invoke();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { Viewer.Dispose(); Session.Dispose(); }
        base.Dispose(disposing);
    }
}
