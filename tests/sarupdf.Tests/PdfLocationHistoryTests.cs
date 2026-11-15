using System.Reflection;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
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

        async Task<JsonElement> CheckPulse(string name, int expectedPage)
        {
            var pulse = await PdfViewportBridge.EvaluateAsync(web.CoreWebView2, """
                (()=>{const e=document.querySelector('[data-sarupdf-location-pulse]');if(!e)return null;
                    const r=e.getBoundingClientRect();return {page:Number(e.getAttribute('data-sarupdf-location-pulse')),
                        x:r.x,y:r.y,width:r.width,height:r.height,viewportWidth:innerWidth,pointerEvents:e.style.pointerEvents};})()
                """);
            Check(pulse is { ValueKind: JsonValueKind.Object }, "Destination pulse is missing");
            var info = pulse!.Value;
            Check(info.GetProperty("page").GetInt32() == expectedPage && info.GetProperty("pointerEvents").GetString() == "none", "Wrong destination or blocking overlay");
            using var stream = new MemoryStream();
            await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
            stream.Position = 0; using var bitmap = new Bitmap(stream);
            bitmap.Save(Path.Combine(root, name + ".png"));
            double scale = bitmap.Width / info.GetProperty("viewportWidth").GetDouble();
            var rect = Rectangle.Intersect(new Rectangle(0, 0, bitmap.Width, bitmap.Height), Rectangle.FromLTRB(
                (int)(info.GetProperty("x").GetDouble()*scale), (int)(info.GetProperty("y").GetDouble()*scale),
                (int)((info.GetProperty("x").GetDouble()+36)*scale)+1, (int)((info.GetProperty("y").GetDouble()+36)*scale)+1));
            int red = 0;
            for (int y = rect.Top; y < rect.Bottom; y++) for (int x = rect.Left; x < rect.Right; x++)
            { var color = bitmap.GetPixel(x, y); if (color.R > 180 && color.R > color.G+40 && color.R > color.B+40) red++; }
            Check(red > 25, $"Red ring is not visibly rendered: {red} pixels");
            return info;
        }

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
        await CheckPulse("link-arrival-pulse", 2);
        await ClickLink(2);
        Check((await view.CapturePageViewAsync())?.Page == 1, "Second reference link did not navigate");
        await CheckPulse("second-link-arrival-pulse", 1);

        var tools = window.MainMenuStrip!.Items.OfType<ToolStripMenuItem>().Single(i => i.Name == "ToolsMenu");
        var back = tools.DropDownItems.OfType<ToolStripMenuItem>().Single(i => i.Name == "PreviousLocation");
        typeof(ToolStripDropDownItem).GetMethod("OnDropDownShow", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(tools, [EventArgs.Empty]);
        await WaitUntilAsync(() => back.Enabled);
        back.PerformClick(); await Task.Delay(500);
        EqualPosition(second, (await view.CapturePageViewAsync())!);
        await CheckPulse("link-return-pulse", 2);
        await view.GoBackAsync(); EqualPosition(first, (await view.CapturePageViewAsync())!);
        var sourcePulse = await CheckPulse("original-reference-pulse", 0);
        var expectedPoint = await PdfViewportBridge.EvaluateAsync(web.CoreWebView2, """
            (()=>{const r=window.viewer.viewport_.getPageScreenRect(0);return {x:r.x+r.width*100/595,y:r.y+r.height*(842-630)/842};})()
            """);
        Check(Math.Abs(sourcePulse.GetProperty("x").GetDouble()+18-expectedPoint!.Value.GetProperty("x").GetDouble()) < 5 &&
            Math.Abs(sourcePulse.GetProperty("y").GetDouble()+18-expectedPoint.Value.GetProperty("y").GetDouble()) < 5,
            "Return marker must identify the original clicked reference");
        await Task.Delay(1000);
        var removed = await PdfViewportBridge.EvaluateAsync(web.CoreWebView2, "document.querySelector('[data-sarupdf-location-pulse]') === null");
        Check(removed is { ValueKind: JsonValueKind.True }, "Destination marker did not disappear");
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
