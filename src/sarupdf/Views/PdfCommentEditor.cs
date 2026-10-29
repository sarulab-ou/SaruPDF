using SaruPdf.Documents;

namespace SaruPdf.Views;

internal sealed class PdfCommentEditor : UserControl
{
    internal PdfEditSession Session { get; }
    internal PdfCommentCanvas Viewer { get; } = new();
    internal event Action? StateChanged;
    internal event Action<Exception>? Failed;

    internal PdfCommentEditor(PdfEditSession session)
    {
        Session = session; Dock = DockStyle.Fill; Controls.Add(Viewer);
        Viewer.SetOrder(session.Pages, 0);
        Viewer.CommentRequested += () => BeginInvoke(() =>
        {
            if (IsDisposed || Viewer.Selection is not { HasSelection: true } selection) return;
            using var dialog = new PdfCommentDialog(selection.SelectedText);
            if (dialog.ShowDialog(this) == DialogResult.OK) AddComment(dialog.CommentText);
        });
        Viewer.ExistingCommentRequested += text => BeginInvoke(() =>
        {
            if (IsDisposed) return;
            using var dialog = new PdfCommentDialog("PDFの注釈", text); dialog.ShowDialog(this);
        });
        Viewer.EditCommentRequested += mark => BeginInvoke(() =>
        {
            if (IsDisposed || Session.Comments.FirstOrDefault(c => c.Id == mark.Id) is not { } current) return;
            using var dialog = new PdfCommentDialog("PDFのコメント", current.Text, editable: true);
            if (dialog.ShowDialog(this) == DialogResult.OK) UpdateComment(current.Id, dialog.CommentText);
        });
        Viewer.Failed += ex => Failed?.Invoke(ex);
        Viewer.CommandFailed += ex => Failed?.Invoke(ex);
    }
    internal async Task LoadAsync(PdfPageViewState? state)
    {
        try
        {
            string snapshot = Session.WriteCommentPreview();
            var map = await Task.Run(() => PdfTextMap.Read(snapshot));
            if (IsDisposed) return;
            Viewer.InheritView(state);
            await Viewer.LoadAsync(snapshot);
            if (IsDisposed) return;
            Viewer.SetTextMap(map); UpdateState();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { if (!IsDisposed) Failed?.Invoke(ex); }
    }
    internal void AddComment(string text) => Execute(() =>
    {
        if (Viewer.Selection is not { HasSelection: true } selection) return;
        Session.AddComment(selection.Regions(), text);
        selection.Set(selection.Caret, false);
    });
    internal void Undo() => Execute(() => Session.Undo());
    internal void UpdateComment(Guid id, string text) => Execute(() => Session.UpdateComment(id, text));
    internal void Redo() => Execute(() => Session.Redo());
    internal void UpdateState() { Viewer.UpdateComments(Session.Comments); StateChanged?.Invoke(); }
    private void Execute(Action action)
    {
        try { action(); UpdateState(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Failed?.Invoke(ex); }
    }
    protected override void Dispose(bool disposing) { if (disposing) { Viewer.Dispose(); Session.Dispose(); } base.Dispose(disposing); }
}
