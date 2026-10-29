using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.WinForms;
using SaruPdf.Documents;
using SaruPdf.Views;
using static SaruPdf.Tests.PdfEditingTests;
using static SaruPdf.Tests.PdfCopyTests;

namespace SaruPdf.Tests;

internal static class PageOperationTests
{
    [DllImport("user32.dll")]
    private static extern bool PrintWindow(nint window, nint dc, uint flags);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint window, int command);

    internal static IEnumerable<(string Name, Action Run)> Cases(string root)
    {
        string Fixture(string name)
        {
            string path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".pdf");
            SamplePdf.Write(path, name); return path;
        }
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
        });
        yield return ("failed multi-PDF import keeps pages and history unchanged", () =>
        {
            using var session = new PdfEditSession(Fixture("Target"));
            string valid = Fixture("Valid");
            Throws<IOException>(() => session.ImportFiles(0, [valid, Path.Combine(root, "missing-import.pdf")]));
            Check(session.PageCount == 3 && !session.IsDirty && !session.CanUndo);
            Throws<ArgumentException>(() => session.ImportFiles(0, []));
            Throws<ArgumentOutOfRangeException>(() => session.ImportFiles(4, [valid]));
            Check(!session.CanUndo);
            session.ImportFiles(0, [valid]); session.Cut(0);
            Throws<InvalidOperationException>(() => session.ImportFiles(0, [valid]));
            Check(session.HasPendingCut && session.PageCount == 5);
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
        using var editor = new PdfEditor(new PdfEditSession(source)); host.Controls.Add(editor); await editor.LoadAsync();
        Exception? failure = null; editor.Failed += ex => failure = ex;
        int requested = -1;
        editor.ImportRequested += index => { requested = index; editor.ImportFiles(index, [source]); };
        using (var menu = editor.Viewer.CreatePageMenu(editor.Viewer.GapPoint(0)))
            menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Name == "ImportPdf").PerformClick();
        Check(requested == 0 && editor.Session.PageCount == 24 && editor.Viewer.SelectedPages.Count == 12);
        await ExpectCanvasColorAsync(editor.Viewer, 0, Color.Red);
        editor.SelectPage(2); await ExpectCanvasColorAsync(editor.Viewer, 1, Color.Lime);
        editor.Undo(); Check(editor.Session.PageCount == 12 && !editor.Session.IsDirty);
        editor.Redo(); editor.SelectPage(3); await ExpectCanvasColorAsync(editor.Viewer, 2, Color.Blue);
        editor.SelectPage(1); editor.Cut();
        using (var menu = editor.Viewer.CreatePageMenu(editor.Viewer.GapPoint(0)))
            Check(!menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Name == "ImportPdf").Enabled);
        editor.Undo();
        var gap = editor.Viewer.GapPoint(1);
        editor.Viewer.AutoScrollPosition = new Point(0, Math.Max(0, gap.Y - editor.Viewer.AutoScrollPosition.Y - 100));
        using (var menu = editor.Viewer.CreatePageMenu(editor.Viewer.GapPoint(1)))
            menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Name == "ImportPdf").PerformClick();
        Check(requested == 1 && editor.Session.PageCount == 36 && editor.Viewer.SelectedPages.SequenceEqual(Enumerable.Range(1, 12)));
        await ExpectCanvasColorAsync(editor.Viewer, 1, Color.Red);
        editor.Undo(); Check(editor.Session.PageCount == 24); editor.Redo(); Check(editor.Session.PageCount == 36);
        Check(failure is null);
        editor.Session.Save(Path.Combine(root, "boundary-import-saved.pdf"));
    });

    internal static Task VerifyInheritedZoomAsync(string root) => OnSurfaceAsync(async host =>
    {
        string path = Path.Combine(root, "inherited-zoom.pdf"); PageNavigationTests.WriteColorPdf(path);
        using var window = new PdfWindow(Path.Combine(root, "inherit-profile-" + Guid.NewGuid().ToString("N"))) { TopLevel = false, Dock = DockStyle.Fill };
        host.Controls.Add(window); window.SetDocument(DocumentPath.Open(path)); window.Show();
        var viewer = (PdfView)typeof(PdfWindow).GetField("viewer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var web = (WebView2)typeof(PdfView).GetField("web", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewer)!;
        await WaitUntilAsync(() => web.CoreWebView2 is not null);
        await PageNavigationTests.ExpectColorAsync(web, Color.Red);
        var file = (ToolStripMenuItem)window.MainMenuStrip!.Items[0];
        Check(!file.DropDownItems.OfType<ToolStripMenuItem>().Any(item => item.ShortcutKeys == (Keys.Control | Keys.N) || item.Text?.Contains("新規ウィンドウ") == true));
        var mode = (ToolStripMenuItem)typeof(PdfWindow).GetField("editMode", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        Check(mode.Text == "ページ操作モード(&M)");
        var command = typeof(PdfWindow).GetMethod("TryHandleEditKey", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Check(!(bool)command.Invoke(window, [Keys.Control | Keys.N])!);
        foreach (double zoom in new[] { .75, 1.25, 1.8 })
        {
            await PageNavigationTests.ExpectColorAsync(web, Color.Red);
            await Task.Delay(1000);
            var applied = await PdfViewportBridge.EvaluateAsync(web.CoreWebView2!, $"(() => {{ try {{ window.viewer.viewport_.setZoom({zoom.ToString(System.Globalization.CultureInfo.InvariantCulture)}); return true; }} catch(e) {{ return String(e); }} }})()");
            if (applied?.ValueKind != System.Text.Json.JsonValueKind.True) throw new Exception("PDF viewport unavailable at " + zoom + ": " + applied);
            await Task.Delay(200);
            var state = await viewer.CapturePageViewAsync();
            Check(state is not null);
            window.ToggleEditMode(); var canvas = Editor(window).Viewer;
            await WaitUntilAsync(() => canvas.Visible && canvas.IsPageRendered(0));
            Check(Math.Abs(canvas.PageBounds(0).Width - state!.Width) <= 2);
            int width = canvas.PageBounds(0).Width;
            host.Width += 90; Check(Math.Abs(canvas.PageBounds(0).Width - width) <= 1);
            Key(window, Keys.Control | Keys.Add); Check(canvas.PageBounds(0).Width > width);
            window.ToggleEditMode();
        }
    });

    internal static Task VerifyArrowSelectionAsync(string root) => OnSurfaceAsync(async host =>
    {
        string path = Path.Combine(root, "arrow-selection.pdf"); PageNavigationTests.WriteColorPdf(path);
        using var window = new PdfWindow() { TopLevel = false, Dock = DockStyle.Fill };
        host.Controls.Add(window); window.SetDocument(DocumentPath.Open(path)); window.Show(); window.ToggleEditMode();
        var editor = Editor(window); var canvas = editor.Viewer;
        await ExpectCanvasColorAsync(canvas, 0, Color.Red);
        Key(window, Keys.Down); Check(canvas.SelectedPages.SequenceEqual([1]));
        await ExpectCanvasColorAsync(canvas, 1, Color.Lime);
        Key(window, Keys.Shift | Keys.Down); Key(window, Keys.Shift | Keys.Down);
        Check(canvas.SelectedPages.SequenceEqual([1, 2, 3]));
        Key(window, Keys.Shift | Keys.Up); Check(canvas.SelectedPages.SequenceEqual([1, 2]));
        Key(window, Keys.Shift | Keys.Up); Key(window, Keys.Shift | Keys.Up);
        Check(canvas.SelectedPages.SequenceEqual([0, 1]));
        Key(window, Keys.Up); Check(canvas.SelectedPages.SequenceEqual([0]));
        Key(window, Keys.Up); Check(canvas.SelectedPages.SequenceEqual([0]));
        canvas.SelectPage(12); Key(window, Keys.Down); Check(canvas.SelectedPages.SequenceEqual([11]));
        canvas.SelectPage(3); Key(window, Keys.Shift | Keys.Up); Check(canvas.SelectedPages.SequenceEqual([1, 2]));
        Key(window, Keys.Shift | Keys.End); Check(canvas.CurrentPage == 11 && canvas.SelectedPages.SequenceEqual(Enumerable.Range(2, 10)));
        Key(window, Keys.Shift | Keys.PageUp); Check(canvas.CurrentPage == 10 && canvas.SelectedPages.SequenceEqual(Enumerable.Range(2, 9)));
        Key(window, Keys.Shift | Keys.Home); Check(canvas.CurrentPage == 0 && canvas.SelectedPages.SequenceEqual([0, 1, 2]));
        Key(window, Keys.PageDown); Check(canvas.CurrentPage == 1 && canvas.SelectedPages.SequenceEqual([1]));
        Key(window, Keys.Home); Check(canvas.CurrentPage == 0 && canvas.SelectedPages.SequenceEqual([0]));
        Key(window, Keys.End); Check(canvas.CurrentPage == 11 && canvas.SelectedPages.SequenceEqual([11]));
        Key(window, Keys.PageDown); Check(canvas.CurrentPage == 11);
        Key(window, Keys.PageUp); Check(canvas.CurrentPage == 10 && canvas.SelectedPages.SequenceEqual([10]));
        editor.Copy(); canvas.SelectEnd(); Key(window, Keys.Up); Check(canvas.SelectedPages.SequenceEqual([11]));
        canvas.SelectPage(1); Key(window, Keys.Shift | Keys.Down); editor.Cut();
        Key(window, Keys.Down); Check(canvas.SelectedPages.SequenceEqual([1]));
        editor.Paste(); Check(editor.Session.PageCount == 12 && !editor.Session.HasPendingCut);
        editor.Undo(); editor.Undo(); Check(!editor.Session.IsDirty);
        Check(!canvas.TryHandlePageMoveKey(Keys.Control | Keys.Down) && !canvas.TryHandlePageMoveKey(Keys.Alt | Keys.Up));
        canvas.SetOrder([], 0); Check(canvas.TryHandlePageMoveKey(Keys.Down) && canvas.SelectedPages.Count == 0);
        Check(canvas.TryHandlePageMoveKey(Keys.Home) && canvas.TryHandlePageMoveKey(Keys.End) && canvas.TryHandlePageMoveKey(Keys.PageDown));
    });

    internal static Task VerifyCurrentPageAsync(string root) => OnSurfaceAsync(async host =>
    {
        string path = Path.Combine(root, "current-page-colors.pdf"); PageNavigationTests.WriteColorPdf(path);
        using var editor = new PdfEditor(new PdfEditSession(path)); host.Controls.Add(editor); await editor.LoadAsync();
        var canvas = editor.Viewer; await ExpectCanvasColorAsync(canvas, 0, Color.Red);
        void Centered(int page)
        {
            var rect = canvas.PageBounds(page);
            if (Math.Abs(rect.Top + rect.Height / 2 - canvas.ClientSize.Height / 2) > 2)
                throw new Exception($"Page {page + 1} not centered: {rect}, viewport {canvas.ClientSize}");
            Check(canvas.CurrentPage == page);
        }
        canvas.TryHandlePageMoveKey(Keys.End); Centered(11);
        canvas.TryHandlePageMoveKey(Keys.Home); Centered(0);
        canvas.SelectPage(6); Centered(5);
        for (int i = 0; i < 9; i++) canvas.TryHandleZoomKey(Keys.Control | Keys.Subtract);
        canvas.TryHandlePageMoveKey(Keys.Home); Centered(0);
        canvas.TryHandlePageMoveKey(Keys.End); Centered(11);
        canvas.SelectPage(1); canvas.TryHandlePageMoveKey(Keys.Shift | Keys.PageDown); Centered(1);
        Check(canvas.SelectedPages.SequenceEqual([0, 1]));
        await ExpectCanvasColorAsync(canvas, 1, Color.Lime);
        using var bitmap = new Bitmap(canvas.Width, canvas.Height); canvas.DrawToBitmap(bitmap, canvas.ClientRectangle);
        var current = canvas.PageBounds(1); var selected = canvas.PageBounds(0);
        Check(bitmap.GetPixel(current.Left + 6, current.Top + current.Height / 2).ToArgb() == Color.FromArgb(220, 100, 0).ToArgb());
        Check(bitmap.GetPixel(selected.Left, Math.Clamp(selected.Bottom - 8, 1, canvas.Height - 2)).ToArgb() == Color.FromArgb(0, 100, 220).ToArgb());
        bitmap.Save(Path.Combine(root, "current-and-selected-pages.png"));
        host.Height += 80; Centered(1);
        canvas.SelectPage(3); await ExpectCanvasColorAsync(canvas, 2, Color.Blue);
        canvas.SelectAt(VisibleCenter(canvas, 2), Keys.Control); Centered(2);
        Check(!canvas.SelectedPages.Contains(2) && canvas.CurrentPage == 2);
        canvas.TryHandlePageMoveKey(Keys.PageDown); Centered(3);
        Check(canvas.SelectedPages.SequenceEqual([3]));
    });

    internal static Task VerifyTitleBarAsync(string root) => OnSurfaceAsync(async _ =>
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        string path = Path.Combine(root, "title-mode.pdf"); SamplePdf.Write(path, "Title mode");
        using var window = new PdfWindow { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), ShowInTaskbar = false };
        window.SetDocument(DocumentPath.Open(path));
        ShowWindow(window.Handle, 4); // SW_SHOWNOACTIVATE: only this test window, outside the desktop.
        await Task.Delay(150);
        int CaptureTitle(string name)
        {
            using var bitmap = new Bitmap(window.Width, window.Height);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                nint dc = graphics.GetHdc();
                try { Check(PrintWindow(window.Handle, dc, 2)); }
                finally { graphics.ReleaseHdc(dc); }
            }
            bitmap.Save(Path.Combine(root, name + ".png"));
            int blue = 0;
            for (int y = 5; y < Math.Min(30, bitmap.Height); y++)
            for (int x = 60; x < bitmap.Width - 180; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (Math.Abs(pixel.R - 32) < 3 && Math.Abs(pixel.G - 88) < 3 && Math.Abs(pixel.B - 150) < 3) blue++;
            }
            return blue;
        }
        int normal = CaptureTitle("title-normal");
        window.ToggleEditMode(); await Task.Delay(200);
        int enabled = CaptureTitle("title-page-operation");
        if (enabled < normal + 100) throw new Exception($"Title pixels: normal {normal}, mode {enabled}");
        window.ToggleEditMode(); await Task.Delay(200);
        Check(CaptureTitle("title-restored") <= normal + 50);
    });
    private static void Check(bool condition) { if (!condition) throw new Exception("Page operation/import/zoom assertion failed."); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
}
