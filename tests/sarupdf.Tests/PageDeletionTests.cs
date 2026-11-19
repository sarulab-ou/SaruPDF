using PdfSharp.Pdf.IO;
using SaruPdf.Documents;
using SaruPdf.Views;
using static SaruPdf.Tests.PdfEditingTests;

namespace SaruPdf.Tests;

internal static class PageDeletionTests
{
    internal static IEnumerable<(string Name, Action Run)> Cases(string root)
    {
        yield return ("page deletion saves remaining content, comments and bookmarks and restores edits with undo", () =>
        {
            string path = Path.Combine(root, "delete-model-" + Guid.NewGuid().ToString("N") + ".pdf");
            SamplePdf.Write(path, "Delete fixture");
            using (var pdf = PdfReader.Open(path, PdfDocumentOpenMode.Modify))
            { pdf.Outlines.Add("Kept page", pdf.Pages[1], true); pdf.Save(path); }
            var original = File.ReadAllBytes(path);
            var map = PdfTextMap.Read(path);
            using var session = new PdfEditSession(path);
            session.AddComment([new(0, [map.Pages[0].Quad(new RectangleF(.1f, .2f, .2f, .02f))])], "Deleted note");
            session.AddComment([new(1, [map.Pages[1].Quad(new RectangleF(.1f, .2f, .2f, .02f))])], "Kept note");
            session.Rotate([2], 90);
            session.Delete([2, 0, 2]);
            Check(session.Pages.SequenceEqual([1]) && session.Comments.Single().Text == "Kept note" && session.Rotations.Count == 0 && !session.HasPendingCut && session.IsDirty);
            string output = Path.Combine(root, "deleted-pages-output.pdf"); session.Save(output);
            PdfCopyTests.Content(output, "Delete fixture:2");
            using (var pdf = PdfReader.Open(output, PdfDocumentOpenMode.Import))
            {
                Check(pdf.Outlines.Count == 1 && pdf.Outlines[0].DestinationPage!.Reference!.ObjectID == pdf.Pages[0].Reference!.ObjectID);
                Check(PdfComments.ReadManaged(pdf).Single().Text == "Kept note");
            }
            Check(session.Undo() && session.Pages.SequenceEqual([0, 1, 2]) && session.Comments.Count == 2 && session.Rotations[2] == 90 && session.IsDirty);
            Check(session.Redo() && session.Pages.SequenceEqual([1]) && session.Comments.Count == 1 && !session.IsDirty);
            var copy = session.Copy([0]); session.PasteCopy(1, copy); session.Delete([1]);
            Check(session.Pages.SequenceEqual([1]) && !session.IsDirty);
            Check(File.ReadAllBytes(path).SequenceEqual(original));
        });
        yield return ("invalid, whole-document and pending-cut deletion leave the edit state intact", () =>
        {
            string path = Path.Combine(root, "delete-invalid-" + Guid.NewGuid().ToString("N") + ".pdf"); SamplePdf.Write(path, "Invalid deletion");
            using var session = new PdfEditSession(path);
            foreach (int[] indexes in new int[][] { [], [-1], [3], [0, 3] }) Throws<ArgumentOutOfRangeException>(() => session.Delete(indexes));
            Throws<InvalidOperationException>(() => session.Delete([0, 1, 2]));
            Check(session.Pages.SequenceEqual([0, 1, 2]) && !session.IsDirty && !session.CanUndo);
            session.Cut(0); Throws<InvalidOperationException>(() => session.Delete([0]));
            Check(session.Pages.SequenceEqual([1, 2]) && session.CutPages.SequenceEqual([0]));
            Check(session.Undo() && !session.IsDirty);
        });
    }
    internal static Task VerifyUiAsync(string root) => OnSurfaceAsync(async host =>
    {
        string path = Path.Combine(root, "delete-ui-" + Guid.NewGuid().ToString("N") + ".pdf"); PageNavigationTests.WriteColorPdf(path);
        using (var pdf = PdfReader.Open(path, PdfDocumentOpenMode.Modify))
        { while (pdf.PageCount > 3) pdf.Pages.RemoveAt(pdf.PageCount - 1); pdf.Save(path); }
        var original = File.ReadAllBytes(path);
        var clipboard = new PdfPageClipboard();
        using var session = new PdfEditSession(path);
        var copied = session.Copy([0]); clipboard.Set(copied);
        using var editor = new PdfEditor(session, clipboard); host.Controls.Add(editor); await editor.LoadAsync();
        var canvas = editor.Viewer;
        await ExpectCanvasColorAsync(canvas, 0, Color.Red);
        canvas.SelectPage(1); RevealPage(canvas, 2); canvas.SelectAt(VisibleCenter(canvas, 2), Keys.Control);
        Check(canvas.SelectedPages.SequenceEqual([0, 2]));
        using (var menu = canvas.CreatePageMenu(VisibleCenter(canvas, 2)))
        {
            var delete = DeleteItem(menu); Check(delete.Enabled && delete.Text == "削除(&D)"); delete.PerformClick();
        }
        Check(session.Pages.SequenceEqual([1]) && canvas.CurrentPage == 0 && canvas.SelectedPages.SequenceEqual([0]) && ReferenceEquals(clipboard.Current, copied));
        await ExpectCanvasColorAsync(canvas, 0, Color.Lime);
        string output = Path.Combine(root, "delete-ui-saved.pdf"); session.Save(output);
        using (var pdf = PdfReader.Open(output, PdfDocumentOpenMode.Import)) Check(pdf.PageCount == 1);
        using (var menu = canvas.CreatePageMenu(VisibleCenter(canvas, 0))) Check(!DeleteItem(menu).Enabled);
        editor.Undo(); Check(session.PageCount == 3 && session.IsDirty);
        editor.Redo(); Check(session.PageCount == 1 && !session.IsDirty); await ExpectCanvasColorAsync(canvas, 0, Color.Lime);
        editor.Undo(); RevealPage(canvas, 1);
        using (var menu = canvas.CreatePageMenu(VisibleCenter(canvas, 1))) Check(canvas.SelectedPages.SequenceEqual([1]) && DeleteItem(menu).Enabled);
        editor.Cut();
        RevealPage(canvas, 0);
        using (var menu = canvas.CreatePageMenu(VisibleCenter(canvas, 0))) Check(!DeleteItem(menu).Enabled);
        editor.Undo();
        canvas.SelectPage(2);
        var key = typeof(PdfPageCanvas).GetMethod("ProcessCmdKey", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Check((bool)key.Invoke(canvas, [new Message(), Keys.Delete])!);
        Check(session.Pages.SequenceEqual([0, 2])); editor.Undo();
        using (var menu = canvas.CreatePageMenu(canvas.GapPoint(0))) Check(!menu.Items.OfType<ToolStripMenuItem>().Any(item => item.Name == "DeletePages"));
        Check(File.ReadAllBytes(path).SequenceEqual(original));
        clipboard.Clear();
        host.Controls.Remove(editor);
        using var window = new PdfWindow { TopLevel = false, Dock = DockStyle.Fill };
        host.Controls.Add(window); window.SetDocument(DocumentPath.Open(path)); window.Show(); window.ToggleEditMode();
        var windowEditor = PdfCopyTests.Editor(window);
        await WaitUntilAsync(() => windowEditor.Visible && windowEditor.Viewer.IsPageRendered(0));
        windowEditor.Viewer.Focus(); PdfCopyTests.Key(window, Keys.Delete);
        await WaitUntilAsync(() => windowEditor.Session.PageCount == 2);
        PdfCopyTests.Key(window, Keys.Control | Keys.Z); await WaitUntilAsync(() => windowEditor.Session.PageCount == 3);
        PdfCopyTests.Key(window, Keys.Control | Keys.F);
        var search = window.Controls.Find("SearchText", true).OfType<TextBox>().Single();
        await WaitUntilAsync(() => search.Focused);
        void OtherFocusDelete()
        {
            typeof(PdfWindow).GetMethod("ProcessCmdKey", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(window, [new Message(), Keys.Delete]);
        }
        OtherFocusDelete(); await Task.Delay(80); Check(windowEditor.Session.PageCount == 3);
        window.Controls.Find("AddressBar", true).OfType<TextBox>().Single().Focus();
        OtherFocusDelete(); await Task.Delay(80); Check(windowEditor.Session.PageCount == 3 && !windowEditor.Session.IsDirty);
        window.ToggleEditMode(); window.ToggleCommentMode();
        var comments = (PdfCommentEditor)typeof(PdfWindow).GetField("commentEditor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(window)!;
        await WaitUntilAsync(() => comments.Visible); comments.Viewer.Focus();
        OtherFocusDelete(); await Task.Delay(80); Check(comments.Session.PageCount == 3 && !comments.Session.IsDirty);
    });
    private static ToolStripMenuItem DeleteItem(ContextMenuStrip menu) => menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Name == "DeletePages");
    private static void Check(bool condition) { if (!condition) throw new Exception("Page deletion assertion failed"); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
}
