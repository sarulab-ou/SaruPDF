using System.Reflection;
using System.Text;
using Microsoft.Web.WebView2.WinForms;
using SaruPdf.Documents;
using SaruPdf.Views;
using static SaruPdf.Tests.PdfEditingTests;

namespace SaruPdf.Tests;

internal static class PdfReloadTests
{
    internal static Task VerifyAsync(string root) => OnSurfaceAsync(async host =>
    {
        string path = Path.Combine(root, "watched-" + Guid.NewGuid().ToString("N") + ".pdf"); PageNavigationTests.WriteColorPdf(path);
        string original = Encoding.ASCII.GetString(File.ReadAllBytes(path));
        byte[] Variant(string color) => Encoding.ASCII.GetBytes(original.Replace("0 0 1 rg", color + " rg", StringComparison.Ordinal));
        using var window = new PdfWindow(Path.Combine(root, "reload-profile-" + Guid.NewGuid().ToString("N"))) { TopLevel = false, Dock = DockStyle.Fill };
        host.Controls.Add(window); window.SetDocument(DocumentPath.Open(path)); window.Show();
        var view = Field<PdfView>(window, "viewer"); var web = Field<WebView2>(view, "web");
        await WaitUntilAsync(() => web.CoreWebView2 is not null); await PageNavigationTests.ExpectColorAsync(web, Color.Red);
        await PdfViewportBridge.EvaluateAsync(web.CoreWebView2, "window.viewer.viewport_.setZoom(1.8)"); await Task.Delay(200);
        await PdfViewportBridge.EvaluateAsync(web.CoreWebView2, "(()=>{const v=window.viewer.viewport_,r=v.getPageScreenRect(2);v.scrollTo({x:v.position.x+r.x+r.width*.6-v.size.width/2,y:v.position.y+r.y+r.height*.4-v.size.height/2});return true})()");
        await PageNavigationTests.ExpectColorAsync(web, Color.Blue);
        var state = (await view.CapturePageViewAsync())!;
        File.WriteAllBytes(path, Variant("1 0 1"));
        await PageNavigationTests.ExpectColorAsync(web, Color.Magenta); Same(state, await view.CapturePageViewAsync());
        string staging = path + ".tmp"; File.WriteAllBytes(staging, Variant("0 1 1")); File.Move(staging, path, true);
        await PageNavigationTests.ExpectColorAsync(web, Color.Cyan); Same(state, await view.CapturePageViewAsync());

        window.ToggleEditMode(); var editor = PdfCopyTests.Editor(window);
        await WaitUntilAsync(() => editor.Visible && editor.Viewer.IsPageRendered(2));
        state = editor.Viewer.CapturePageView()!;
        File.WriteAllBytes(staging, Variant("1 1 0")); File.Move(staging, path, true);
        await WaitUntilAsync(() => !ReferenceEquals(PdfCopyTests.Editor(window), editor) && PdfCopyTests.Editor(window).Visible);
        editor = PdfCopyTests.Editor(window); Same(state, editor.Viewer.CapturePageView());
        await ExpectCanvasColorAsync(editor.Viewer, 2, Color.Yellow);

        // Invalid intermediate writes must not release the existing session.
        File.WriteAllText(path, "%PDF-1.4\npartially written"); await Task.Delay(600);
        Check(ReferenceEquals(PdfCopyTests.Editor(window), editor));
        File.WriteAllBytes(path, Variant("1 0 1"));
        await WaitUntilAsync(() => !ReferenceEquals(PdfCopyTests.Editor(window), editor) && PdfCopyTests.Editor(window).Visible);
        editor = PdfCopyTests.Editor(window);
        editor.Viewer.SelectPage(3); editor.Rotate(90);
        File.WriteAllBytes(staging, Variant("0 1 1")); File.Move(staging, path, true);
        await WaitUntilAsync(() => typeof(PdfWindow).GetField("pendingSource", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window) is not null);
        Check(ReferenceEquals(PdfCopyTests.Editor(window), editor) && editor.Session.IsDirty);
        File.WriteAllBytes(path, Variant("1 0 1"));
        await WaitUntilAsync(() => typeof(PdfWindow).GetField("pendingSource", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window) is null);
        Check(ReferenceEquals(PdfCopyTests.Editor(window), editor) && editor.Session.IsDirty);
        File.WriteAllBytes(path, Variant("0 1 1"));
        await WaitUntilAsync(() => typeof(PdfWindow).GetField("pendingSource", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window) is not null);

        // Save-as moves the watch to the new path; the old path no longer applies.
        string saved = path + ".saved.pdf"; Check(window.SaveToPath(saved));
        await Task.Delay(500); File.WriteAllBytes(path, Variant("1 1 0")); await Task.Delay(600);
        Check(ReferenceEquals(PdfCopyTests.Editor(window), editor));
        state = editor.Viewer.CapturePageView()!;
        File.WriteAllBytes(saved, Variant("1 0 1"));
        await WaitUntilAsync(() => !ReferenceEquals(PdfCopyTests.Editor(window), editor) && PdfCopyTests.Editor(window).Visible);
        Same(state, PdfCopyTests.Editor(window).Viewer.CapturePageView());

        window.ToggleCommentMode(); var comment = Field<PdfCommentEditor>(window, "commentEditor");
        await WaitUntilAsync(() => comment.Visible && comment.Viewer.Selection is not null);
        state = comment.Viewer.CapturePageView()!;
        File.WriteAllBytes(saved, Variant("0 1 1"));
        await WaitUntilAsync(() => !ReferenceEquals(Field<PdfCommentEditor>(window, "commentEditor"), comment) && Field<PdfCommentEditor>(window, "commentEditor").Visible);
        comment = Field<PdfCommentEditor>(window, "commentEditor"); Same(state, comment.Viewer.CapturePageView());
        await ExpectCanvasColorAsync(comment.Viewer, 2, Color.Cyan);
        host.Controls.Remove(window);
    });
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;
    private static void Same(PdfPageViewState expected, PdfPageViewState? actual)
    {
        if (actual is null || actual.Page != expected.Page || Math.Abs(actual.Width - expected.Width) > 3 || Math.Abs(actual.CenterX - expected.CenterX) * expected.Width > 5 || Math.Abs(actual.CenterY - expected.CenterY) * expected.Width > 5)
            throw new Exception($"Reload moved viewport: expected {expected}, actual {actual}");
    }
    private static void Check(bool condition) { if (!condition) throw new Exception("PDF auto reload assertion failed."); }
}
