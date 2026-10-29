using System.Reflection;
using Microsoft.Web.WebView2.WinForms;
using SaruPdf.Documents;
using SaruPdf.Views;
using static SaruPdf.Tests.PdfEditingTests;
using static SaruPdf.Tests.PdfCopyTests;

namespace SaruPdf.Tests;

internal static class NewDocumentTests
{
    internal static IEnumerable<(string Name, Action Run)> Cases(string root)
    {
        string Fixture(string name)
        {
            string path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".pdf");
            SamplePdf.Write(path, name); return path;
        }
        yield return ("new document has zero pages and saves copied pages without an extra blank", () =>
        {
            string source = Fixture("Source");
            using var original = new PdfEditSession(source);
            using var created = PdfEditSession.CreateNew();
            Check(created.PageCount == 0 && !created.IsDirty && created.SourcePath == "" && created.RenderPages().Count == 0);
            string saved = Path.Combine(root, "新規 保存 #100% &.pdf");
            Throws<InvalidOperationException>(() => created.Save(saved));
            created.PasteCopy(0, original.Copy([0, 2]));
            Check(created.IsDirty && created.PageCount == 2 && created.RenderPages().Count == 2);
            created.Save(saved); Content(saved, "Source:1", "Source:3");
            created.Undo(); Check(created.PageCount == 0 && created.IsDirty);
            created.Redo(); Check(!created.IsDirty && created.PageCount == 2);
            created.Cut(0); created.Paste(1); created.Save(saved); Content(saved, "Source:3", "Source:1");
        });
        yield return ("boundary import inserts all selected PDFs in one undoable operation", () =>
        {
            string a = Fixture("A"), b = Fixture("B"), target = Fixture("Target");
            byte[] first = File.ReadAllBytes(a), second = File.ReadAllBytes(b);
            using var session = new PdfEditSession(target);
            Check(session.ImportFiles(1, [a, b]) == 6 && session.PageCount == 9);
            session.Save(target);
            Content(target, "Target:1", "A:1", "A:2", "A:3", "B:1", "B:2", "B:3", "Target:2", "Target:3");
            session.Undo(); Check(session.PageCount == 3 && session.IsDirty);
            session.Redo(); Check(session.PageCount == 9 && !session.IsDirty);
            Check(File.ReadAllBytes(a).SequenceEqual(first) && File.ReadAllBytes(b).SequenceEqual(second));
            using var created = PdfEditSession.CreateNew(); created.ImportFiles(0, [b]); created.Save(Path.Combine(root, "new-import.pdf"));
            Content(created.SourcePath, "B:1", "B:2", "B:3");
        });
        yield return ("failed multi-PDF import keeps pages and history unchanged", () =>
        {
            using var session = PdfEditSession.CreateNew();
            string valid = Fixture("Valid");
            Throws<IOException>(() => session.ImportFiles(0, [valid, Path.Combine(root, "missing-import.pdf")]));
            Check(session.PageCount == 0 && !session.IsDirty && !session.CanUndo);
            Throws<ArgumentException>(() => session.ImportFiles(0, []));
            Throws<ArgumentOutOfRangeException>(() => session.ImportFiles(1, [valid]));
            Check(!session.CanUndo);
            session.ImportFiles(0, [valid]); session.Cut(0);
            Throws<InvalidOperationException>(() => session.ImportFiles(0, [valid]));
            Check(session.HasPendingCut && session.PageCount == 2);
        });
        yield return ("untitled windows cannot be reused by a later file-open request", () =>
        {
            var catalog = new WindowCatalog<object>(); var welcome = new object(); var untitled = new object();
            catalog.Add(welcome); catalog.Add(untitled); catalog.MarkOccupied(untitled);
            Check(ReferenceEquals(catalog.EmptyWindow, welcome));
            catalog.Assign("existing.pdf", welcome); Check(catalog.EmptyWindow is null);
            catalog.Reassign("new.pdf", untitled);
            Check(catalog.TryFind("new.pdf", out var found) && ReferenceEquals(found, untitled));
            catalog.Remove(untitled); Check(catalog.TryFind("existing.pdf", out _) && catalog.EmptyWindow is null);
        });
    }

    internal static Task VerifyZoomAsync(string root) => OnSurfaceAsync(async host =>
    {
        string path = Path.Combine(root, "zoom-import-colors.pdf"); PageNavigationTests.WriteColorPdf(path);
        using var window = new PdfWindow(Path.Combine(root, "zoom-import-profile-" + Guid.NewGuid().ToString("N"))) { TopLevel = false, Dock = DockStyle.Fill };
        host.Controls.Add(window); window.SetDocument(DocumentPath.Open(path)); window.Show(); window.ToggleEditMode();
        var editor = Editor(window); var canvas = editor.Viewer;
        await ExpectCanvasColorAsync(canvas, 0, Color.Red);
        int width = canvas.PageBounds(0).Width;
        Key(window, Keys.Control | Keys.Oemplus); Check(canvas.PageBounds(0).Width > width);
        Key(window, Keys.Control | Keys.OemMinus); Check(Math.Abs(canvas.PageBounds(0).Width - width) <= 1);
        Key(window, Keys.Control | Keys.Shift | Keys.Oemplus); Check(canvas.PageBounds(0).Width > width);
        Key(window, Keys.Control | Keys.Subtract); Check(Math.Abs(canvas.PageBounds(0).Width - width) <= 1);
        Key(window, Keys.Control | Keys.Add); Check(canvas.PageBounds(0).Width > width);
        Key(window, Keys.Control | Keys.OemMinus);
        Check(!canvas.TryHandleZoomKey(Keys.Oemplus) && !canvas.TryHandleZoomKey(Keys.Control | Keys.Alt | Keys.Add));
        canvas.SelectPage(3); await ExpectCanvasColorAsync(canvas, 2, Color.Blue);
        var page = canvas.PageBounds(2); int center = Math.Min(canvas.ClientSize.Height / 2, page.Bottom - 1);
        Key(window, Keys.Control | Keys.Add); Check(canvas.PageBounds(2).Top < center && canvas.PageBounds(2).Bottom > center);
        Key(window, Keys.Control | Keys.Subtract); canvas.SelectPage(1);
        using (var menu = canvas.CreatePageMenu(canvas.GapPoint(0)))
            Check(menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Name == "ImportPdf").Enabled);
    });

    internal static Task VerifyBoundaryImportAsync(string root) => OnSurfaceAsync(async host =>
    {
        string source = Path.Combine(root, "boundary-import-colors.pdf"); PageNavigationTests.WriteColorPdf(source);
        using var editor = new PdfEditor(PdfEditSession.CreateNew()); host.Controls.Add(editor); await editor.LoadAsync();
        Exception? failure = null; editor.Failed += ex => failure = ex;
        int requested = -1;
        editor.ImportRequested += index => { requested = index; editor.ImportFiles(index, [source]); };
        using (var menu = editor.Viewer.CreatePageMenu(new Point(100, 100)))
            menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Name == "ImportPdf").PerformClick();
        Check(requested == 0 && editor.Session.PageCount == 12 && editor.Viewer.SelectedPages.Count == 12);
        await ExpectCanvasColorAsync(editor.Viewer, 0, Color.Red);
        editor.SelectPage(2); await ExpectCanvasColorAsync(editor.Viewer, 1, Color.Lime);
        editor.Undo(); Check(editor.Session.PageCount == 0 && !editor.Session.IsDirty);
        editor.Redo(); editor.SelectPage(3); await ExpectCanvasColorAsync(editor.Viewer, 2, Color.Blue);
        editor.SelectPage(1); editor.Cut();
        using (var menu = editor.Viewer.CreatePageMenu(editor.Viewer.GapPoint(0)))
            Check(!menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Name == "ImportPdf").Enabled);
        editor.Undo();
        var gap = editor.Viewer.GapPoint(1);
        editor.Viewer.AutoScrollPosition = new Point(0, Math.Max(0, gap.Y - editor.Viewer.AutoScrollPosition.Y - 100));
        using (var menu = editor.Viewer.CreatePageMenu(editor.Viewer.GapPoint(1)))
            menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Name == "ImportPdf").PerformClick();
        Check(requested == 1 && editor.Session.PageCount == 24 && editor.Viewer.SelectedPages.SequenceEqual(Enumerable.Range(1, 12)));
        await ExpectCanvasColorAsync(editor.Viewer, 1, Color.Red);
        editor.Undo(); Check(editor.Session.PageCount == 12); editor.Redo(); Check(editor.Session.PageCount == 24);
        Check(failure is null);
        editor.Session.Save(Path.Combine(root, "boundary-import-saved.pdf"));
    });

    internal static Task VerifyNewWindowAsync(string root) => OnSurfaceAsync(async host =>
    {
        string sourcePath = Path.Combine(root, "new-window-colors.pdf"); PageNavigationTests.WriteColorPdf(sourcePath);
        var clipboard = new PdfPageClipboard(); var catalog = new WindowCatalog<PdfWindow>();
        using var source = new PdfWindow(Path.Combine(root, "new-source-profile-" + Guid.NewGuid().ToString("N")), clipboard) { TopLevel = false, Dock = DockStyle.Fill };
        host.Controls.Add(source); catalog.Add(source); catalog.Assign(sourcePath, source);
        source.SetDocument(DocumentPath.Open(sourcePath)); source.Show(); source.ToggleEditMode();
        var input = Editor(source); await ExpectCanvasColorAsync(input.Viewer, 0, Color.Red);
        input.SelectPage(2); RevealPage(input.Viewer, 2); input.Viewer.SelectAt(VisibleCenter(input.Viewer, 2), Keys.Control);
        Key(source, Keys.Control | Keys.C); await WaitUntilAsync(() => clipboard.Current?.PageCount == 2);
        List<PdfWindow> created = [];
        void CreateNew()
        {
            var window = new PdfWindow(Path.Combine(root, "new-target-profile-" + Guid.NewGuid().ToString("N")), clipboard) { TopLevel = false, Dock = DockStyle.Fill };
            created.Add(window); host.Controls.Add(window); catalog.Add(window); catalog.MarkOccupied(window);
            window.NewWindowRequested += CreateNew;
            window.CanUseDocumentPath = path => !catalog.TryFind(path, out var current) || ReferenceEquals(current, window);
            window.DocumentPathChanged += document => catalog.Reassign(document.Path, window);
            window.FormClosed += (_, _) => catalog.Remove(window);
            window.SetNewDocument(); window.Show(); window.BringToFront();
        }
        source.NewWindowRequested += CreateNew;
        try
        {
            Key(source, Keys.Control | Keys.N); await WaitUntilAsync(() => created.Count == 1);
            var target = created[0]; var output = Editor(target);
            Check(target.Text == "無題" && output.Session.PageCount == 0 && !output.Session.IsDirty && output.CanPaste);
            Check(catalog.EmptyWindow is null && !source.IsDisposed);
            Key(target, Keys.Control | Keys.V); await WaitUntilAsync(() => output.Session.PageCount == 2);
            await ExpectCanvasColorAsync(output.Viewer, 0, Color.Lime);
            output.SelectPage(2); await ExpectCanvasColorAsync(output.Viewer, 1, Color.Blue);
            Check(!target.SaveToPath(sourcePath));
            string savedPath = Path.Combine(root, "CtrlN-保存 #100% &.pdf"); Check(target.SaveToPath(savedPath));
            Check(target.Text == $"{Path.GetFileName(savedPath)}（{savedPath}）" && !output.Session.IsDirty);
            Check(catalog.TryFind(savedPath, out var savedWindow) && ReferenceEquals(savedWindow, target));
            target.ToggleEditMode();
            var readViewer = (PdfView)typeof(PdfWindow).GetField("viewer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
            var web = (WebView2)typeof(PdfView).GetField("web", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(readViewer)!;
            await WaitUntilAsync(() => web.CoreWebView2 is not null && web.Source?.IsFile == true);
            await PageNavigationTests.ExpectColorAsync(web, Color.Lime);
            Key(target, Keys.Control | Keys.N); await WaitUntilAsync(() => created.Count == 2);
            Check(created[1].Text == "無題" && Editor(created[1]).Session.PageCount == 0);
            created[1].Close(); target.Close(); source.Close();
        }
        finally { foreach (var window in created) window.Dispose(); }
    });

    private static void Check(bool condition) { if (!condition) throw new Exception("New document/import/zoom assertion failed."); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
}
