using System.Reflection;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using SaruPdf.Documents;
using SaruPdf.Views;
using static SaruPdf.Tests.PdfEditingTests;
using static SaruPdf.Tests.PdfCopyTests;

namespace SaruPdf.Tests;

internal static class PdfSearchTests
{
    internal static IEnumerable<(string Name, Action Run)> Cases(string root)
    {
        yield return ("search uses glyph positions across pages and supports Japanese and case-insensitive text", () =>
        {
            string path = Path.Combine(root, "search-text.pdf"); SamplePdf.Write(path, "Search fixture");
            var map = PdfTextMap.Read(path);
            var matches = PdfSearch.Find(map, "sArUpDf");
            Check(matches.Count == 3 && matches.Select(match => match.Page).SequenceEqual([0, 1, 2]));
            Check(matches.All(match => match.Bounds.Count == 1 && match.Bounds[0].Width > 0));
            Check(PdfSearch.Find(map, "unfindable text").Count == 0 && PdfSearch.Find(map, "").Count == 0);
            string japanese = Path.Combine(root, "search-japanese.pdf"); SamplePdf.WriteJapanese(japanese);
            var jp = PdfSearch.Find(PdfTextMap.Read(japanese), "日本語"); Check(jp.Count == 1 && jp[0].Bounds.Count == 1);
            var synthetic = new PdfTextMap();
            synthetic.Glyphs.Add(new("😀", 0, 0, new RectangleF(.1f, .1f, .02f, .02f)));
            synthetic.Glyphs.Add(new("A", 0, 1, new RectangleF(.1f, .2f, .01f, .02f)));
            synthetic.Glyphs.Add(new("😀", 1, 2, new RectangleF(.2f, .1f, .02f, .02f)));
            Check(PdfSearch.Find(synthetic, "😀").Count == 2);
            var acrossLines = PdfSearch.Find(synthetic, "😀A");
            Check(acrossLines.Count == 1 && acrossLines[0].Bounds.Count == 2);
            Check(PdfSearch.Find(synthetic, "A😀").Count == 0); // Never join separate pages.
        });
    }

    internal static Task VerifyUiAsync(string root) => OnSurfaceAsync(async host =>
    {
        string path = Path.Combine(root, "search-ui-" + Guid.NewGuid().ToString("N") + ".pdf"); SamplePdf.Write(path, "Search UI");
        byte[] original = File.ReadAllBytes(path);
        using var window = new PdfWindow(Path.Combine(root, "search-profile-" + Guid.NewGuid().ToString("N"))) { TopLevel = false, Dock = DockStyle.Fill };
        host.Controls.Add(window); window.SetDocument(DocumentPath.Open(path)); window.Show();
        var viewer = (PdfView)typeof(PdfWindow).GetField("viewer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var web = (WebView2)typeof(PdfView).GetField("web", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewer)!;
        await WaitUntilAsync(() => web.CoreWebView2 is not null && web.Source?.IsFile == true);
        await Task.Delay(1500);
        var eventArgs = new KeyEventArgs(Keys.Control | Keys.F);
        typeof(Control).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(web, [eventArgs]);
        Check(eventArgs.Handled);
        var panel = window.Controls.Find("SearchPanel", true).OfType<Panel>().Single();
        var text = window.Controls.Find("SearchText", true).OfType<TextBox>().Single();
        var next = window.Controls.Find("SearchNext", true).OfType<Button>().Single();
        var previous = window.Controls.Find("SearchPrevious", true).OfType<Button>().Single();
        var status = window.Controls.Find("SearchStatus", true).OfType<Label>().Single();
        await WaitUntilAsync(() => panel.Visible && text.Focused);
        text.Text = "SaruPDF"; Key(window, Keys.Enter);
        await WaitUntilAsync(() => status.Text == "1 / 3" && next.Enabled);
        Check(web.CoreWebView2.Find.MatchCount == 3 && web.CoreWebView2.Find.ActiveMatchIndex == 1);
        using (var stream = new MemoryStream())
        {
            await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
            stream.Position = 0; using var image = new Bitmap(stream);
            int highlighted = 0;
            for (int y = 0; y < image.Height; y++) for (int x = 0; x < image.Width; x++)
            { var color = image.GetPixel(x, y); if (color.R > 190 && color.G > 110 && color.B < 90) highlighted++; }
            Check(highlighted > 10);
        }
        next.PerformClick(); await WaitUntilAsync(() => status.Text == "2 / 3" && next.Enabled);
        previous.PerformClick(); await WaitUntilAsync(() => status.Text == "1 / 3" && next.Enabled);
        text.Focus(); Key(window, Keys.Shift | Keys.F3); await WaitUntilAsync(() => status.Text == "3 / 3" && next.Enabled);
        Key(window, Keys.F3); await WaitUntilAsync(() => status.Text == "1 / 3" && next.Enabled);
        text.Text = "not-present-anywhere"; next.PerformClick(); await WaitUntilAsync(() => status.Text == "一致なし" && next.Enabled);
        window.Controls.Find("SearchClose", true).OfType<Button>().Single().PerformClick(); Check(!panel.Visible);

        window.ToggleEditMode(); var editor = Editor(window); var canvas = editor.Viewer;
        await WaitUntilAsync(() => editor.Visible && canvas.IsPageRendered(0));
        Key(window, Keys.Control | Keys.F); await WaitUntilAsync(() => panel.Visible && text.Focused);
        text.Text = "SaruPDF"; next.PerformClick(); await WaitUntilAsync(() => status.Text == "1 / 3" && next.Enabled);
        var active = (PdfSearchMatch?)typeof(PdfPageCanvas).GetField("searchMatch", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(canvas);
        Check(active is { Page: 0 } && active.Bounds.Count > 0);
        using (var image = new Bitmap(canvas.Width, canvas.Height))
        {
            canvas.DrawToBitmap(image, canvas.ClientRectangle);
            int colored = 0;
            var page = canvas.PageBounds(0); var area = active!.Bounds[0];
            var rect = Rectangle.Intersect(canvas.ClientRectangle, Rectangle.Round(new RectangleF(page.Left + page.Width * area.Left, page.Top + page.Height * area.Top,
                page.Width * area.Width, page.Height * area.Height)));
            for (int y = rect.Top; y < rect.Bottom; y++) for (int x = rect.Left; x < rect.Right; x++)
            { var color = image.GetPixel(x, y); if (color.R > 150 && color.B > color.G + 15 && color.B > 180) colored++; }
            Check(colored > 2);
        }
        next.PerformClick(); await WaitUntilAsync(() => status.Text == "2 / 3" && next.Enabled);
        active = (PdfSearchMatch?)typeof(PdfPageCanvas).GetField("searchMatch", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(canvas);
        Check(active?.Page == 1);
        canvas.SelectPage(1); editor.Rotate(90); Check(editor.Session.IsDirty && status.Text == "");
        next.PerformClick(); await WaitUntilAsync(() => status.Text == "1 / 3" && next.Enabled);
        string saved = Path.Combine(root, "search-edited-" + Guid.NewGuid().ToString("N") + ".pdf"); Check(window.SaveToPath(saved));
        window.ToggleEditMode(); window.ToggleCommentMode();
        var comment = (PdfCommentEditor)typeof(PdfWindow).GetField("commentEditor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        await WaitUntilAsync(() => comment.Visible && comment.Viewer.Selection is not null);
        next.PerformClick(); await WaitUntilAsync(() => status.Text == "1 / 3" && next.Enabled);
        active = (PdfSearchMatch?)typeof(PdfPageCanvas).GetField("searchMatch", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(comment.Viewer);
        Check(active?.Page == 0 && File.ReadAllBytes(path).SequenceEqual(original));
        window.Controls.Find("SearchClose", true).OfType<Button>().Single().PerformClick();
        Check(!panel.Visible && typeof(PdfPageCanvas).GetField("searchMatch", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(comment.Viewer) is null);
        window.ToggleCommentMode(); host.Controls.Remove(window);

        string japanese = Path.Combine(root, "search-view-japanese.pdf"); SamplePdf.WriteJapanese(japanese);
        using var jpViewer = new PdfView(DocumentPath.Open(japanese), Path.Combine(root, "search-jp-profile-" + Guid.NewGuid().ToString("N"))) { Dock = DockStyle.Fill };
        host.Controls.Add(jpViewer); await jpViewer.LoadAsync();
        await Task.Delay(1500);
        Check(await jpViewer.FindAsync("日本語", false));
        var jpWeb = (WebView2)typeof(PdfView).GetField("web", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(jpViewer)!;
        await WaitUntilAsync(() => jpWeb.CoreWebView2.Find.MatchCount == 1);
        jpViewer.StopFind(); host.Controls.Remove(jpViewer);
    });
    private static void Check(bool condition) { if (!condition) throw new Exception("PDF search assertion failed."); }
}
