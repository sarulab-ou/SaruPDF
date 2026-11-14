using System.Reflection;
using System.Text.Json;
using Microsoft.Web.WebView2.WinForms;
using SaruPdf.Documents;
using SaruPdf.Views;
using static SaruPdf.Tests.PdfEditingTests;

namespace SaruPdf.Tests;

internal static class PdfLocationHistoryTests
{
    internal static Task VerifyAsync(string root) => OnSurfaceAsync(async host =>
    {
        string path = Path.Combine(root, "location-links-" + Guid.NewGuid().ToString("N") + ".pdf");
        SamplePdf.Write(path, "Reference link", links: true);
        var bytes = File.ReadAllBytes(path);
        using var window = new PdfWindow(Path.Combine(root, "location-profile-" + Guid.NewGuid().ToString("N"))) { TopLevel = false, Dock = DockStyle.Fill };
        host.Controls.Add(window); window.SetDocument(DocumentPath.Open(path)); window.Show();
        var view = Field<PdfView>(window, "viewer"); var web = Field<WebView2>(view, "web");
        await WaitUntilAsync(() => Field<bool>(view, "ready"));
        await Field<Task>(view, "initializingHistory");
        Check(!await view.HasPreviousLocationAsync(), "New document must have no history");
        await PdfViewportBridge.EvaluateAsync(web.CoreWebView2, "window.viewer.viewport_.setZoom(1.5)"); await Task.Delay(150);
        await PdfViewportBridge.EvaluateAsync(web.CoreWebView2, "window.viewer.viewport_.scrollTo({x:25,y:80})"); await Task.Delay(100);
        var first = (await view.CapturePageViewAsync())!;
        Check(!await view.HasPreviousLocationAsync(), "Ordinary scrolling/zoom must not add history");

        async Task ClickLink(int page)
        {
            var point = await PdfViewportBridge.EvaluateAsync(web.CoreWebView2, $$"""
                (()=>{const r=window.viewer.viewport_.getPageScreenRect({{page}});return {x:r.x+r.width*100/595,y:r.y+r.height*(842-630)/842}; })()
                """);
            double x = point!.Value.GetProperty("x").GetDouble(), y = point.Value.GetProperty("y").GetDouble();
            foreach (string type in new[] { "mousePressed", "mouseReleased" })
                await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type, x, y, button = "left", clickCount = 1 }));
            await Task.Delay(500);
        }
        await ClickLink(0);
        var second = (await view.CapturePageViewAsync())!;
        Check(second.Page == 2 && await view.HasPreviousLocationAsync(), $"Reference link did not navigate: {second}");
        await ClickLink(2);
        Check((await view.CapturePageViewAsync())?.Page == 1, "Second reference link did not navigate");

        var tools = window.MainMenuStrip!.Items.OfType<ToolStripMenuItem>().Single(i => i.Name == "ToolsMenu");
        var back = tools.DropDownItems.OfType<ToolStripMenuItem>().Single(i => i.Name == "PreviousLocation");
        typeof(ToolStripDropDownItem).GetMethod("OnDropDownShow", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(tools, [EventArgs.Empty]);
        await WaitUntilAsync(() => back.Enabled);
        back.PerformClick(); await Task.Delay(500);
        EqualPosition(second, (await view.CapturePageViewAsync())!);
        await view.GoBackAsync(); EqualPosition(first, (await view.CapturePageViewAsync())!);
        Check(!await view.HasPreviousLocationAsync(), "Nested navigation must create one history entry per link");
        await view.GoBackAsync(); EqualPosition(first, (await view.CapturePageViewAsync())!);
        await ClickLink(0); Check(await view.HasPreviousLocationAsync(), "History did not resume after returning");
        view.NavigateDocument(DocumentPath.Open(path));
        await WaitUntilAsync(() => Field<bool>(view, "ready")); await Field<Task>(view, "initializingHistory");
        Check(!await view.HasPreviousLocationAsync(), "Reload must discard stale history");
        Check(File.ReadAllBytes(path).SequenceEqual(bytes), "Navigation changed the PDF");
    });
    private static void EqualPosition(PdfPageViewState expected, PdfPageViewState actual)
    {
        Check(expected.Page == actual.Page && Math.Abs(expected.Width - actual.Width) < 3 &&
            Math.Abs(expected.CenterX - actual.CenterX) * expected.Width < 5 && Math.Abs(expected.CenterY - actual.CenterY) * expected.Width < 5,
            $"Previous location differs: expected {expected}, actual {actual}");
    }
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
