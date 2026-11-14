using System.Reflection;
using System.Text.Json;
using Microsoft.Web.WebView2.WinForms;
using SaruPdf.Instances;
using SaruPdf.Updates;
using SaruPdf.Views;
using static SaruPdf.Tests.PdfEditingTests;

namespace SaruPdf.Tests;

internal static class UpdateSessionTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases(string root)
    {
        yield return ("update session retains Unicode PDF paths, modes, viewport and active window", () =>
        {
            string pdf = Path.GetFullPath(Path.Combine(root, "日本語 it's # & document.pdf"));
            var state = new PdfWindowState(pdf, PdfDisplayMode.Comments, new(8, 1800, .65, .7), new(100, 200, 1200, 800, true), 123, 115);
            string path = Path.Combine(root, "update-session.json");
            UpdateSessionFile.Write(path, new(1, [state, new(null, PdfDisplayMode.View, null)], 0));
            var restored = UpdateSessionFile.Read(path);
            Check(restored.Windows[0] == state && restored.ActiveIndex == 0 && restored.Windows[1].Path is null);
            // Missing PDFs are validated when opening each window, allowing the
            // other documents in the same session to be restored independently.
            Check(!File.Exists(pdf)); return Task.CompletedTask;
        });
        yield return ("update session rejects invalid versions, modes and nonfinite viewport values", () =>
        {
            string path = Path.Combine(root, "invalid-update-session.json");
            var state = new PdfWindowState(null, PdfDisplayMode.View, new(0, 1200));
            foreach (var session in new[]
            {
                new UpdateSession(9, [state], 0), new(1, [state], 5),
                new(1, [state with { Mode = (PdfDisplayMode)99 }], 0),
                new(1, [state with { View = new(0, double.NaN) }], 0)
            }) Throws(() => UpdateSessionFile.Write(path, session));
            return Task.CompletedTask;
        });
        yield return ("instance forwarding preserves update session and accepts legacy open requests", async () =>
        {
            var session = new UpdateSession(1, [new(Path.GetFullPath(Path.Combine(root, "forwarded.pdf")), PdfDisplayMode.Pages, new(1, 1600, .6, .4))], 0);
            var request = OpenRequest.FromArguments(session.Windows.Select(w => w.Path!).ToArray(), root) with { Session = session };
            using var data = new MemoryStream(); await PipeProtocol.WriteAsync(data, request, default); data.Position = 0;
            var copy = await PipeProtocol.ReadAsync<OpenRequest>(data, default);
            Check(copy.Session?.Windows[0] == session.Windows[0]);
            var legacy = JsonSerializer.Deserialize<OpenRequest>("{\"Id\":\"" + Guid.NewGuid() + "\",\"Paths\":[],\"Errors\":[]}");
            Check(legacy?.Session is null);
        });
    }
    internal static Task VerifyWindowsAsync(string root) => OnSurfaceAsync(async host =>
    {
        List<PdfWindowState> states = [];
        foreach (var mode in Enum.GetValues<PdfDisplayMode>())
        {
            string path = Path.Combine(root, "restore-" + mode + " 日本語 # &.pdf"); SamplePdf.Write(path, "Restore session");
            using var source = new PdfWindow(Path.Combine(root, "capture-profile-" + Guid.NewGuid().ToString("N"))) { TopLevel = false, Dock = DockStyle.Fill };
            host.Controls.Add(source); source.SetDocument(SaruPdf.Documents.DocumentPath.Open(path)); source.Show();
            var view = Field<PdfView>(source, "viewer"); var web = Field<WebView2>(view, "web");
            await WaitUntilAsync(() => web.CoreWebView2 is not null);
            await WaitUntilAsync(() => Field<bool>(view, "ready")); await Task.Delay(400);
            await PdfViewportBridge.EvaluateAsync(web.CoreWebView2, "window.viewer.viewport_.setZoom(1.8)"); await Task.Delay(100);
            await PdfViewportBridge.EvaluateAsync(web.CoreWebView2, "(()=>{let v=window.viewer.viewport_,r=v.getPageScreenRect(1);v.scrollTo({x:v.position.x+r.x+r.width*.6-v.size.width/2,y:v.position.y+r.y+r.height*.4-v.size.height/2});return true})()");
            await Task.Delay(100);
            if (mode == PdfDisplayMode.Pages) { source.ToggleEditMode(); await WaitUntilAsync(() => Field<PdfEditor>(source, "editor").Visible); }
            if (mode == PdfDisplayMode.Comments)
            {
                source.ToggleCommentMode(); var editor = Field<PdfCommentEditor>(source, "commentEditor");
                await WaitUntilAsync(() => editor.Visible && editor.Viewer.Selection is not null);
                var selection = editor.Viewer.Selection!; int caret = selection.Caret;
                selection.Set(caret + 2, false); selection.Set(caret + 8, true);
            }
            source.FreezeForUpdate(true);
            states.Add(await source.CaptureForUpdateAsync());
            source.FreezeForUpdate(false); host.Controls.Remove(source);
        }
        string resume = Path.Combine(root, "restore-window-state.json"); UpdateSessionFile.Write(resume, new(1, states.ToArray(), 2));
        foreach (var state in UpdateSessionFile.Read(resume).Windows)
        {
            using var restored = new PdfWindow(Path.Combine(root, "restore-profile-" + Guid.NewGuid().ToString("N"))) { TopLevel = false, Dock = DockStyle.Fill };
            host.Controls.Add(restored); restored.SetDocument(SaruPdf.Documents.DocumentPath.Open(state.Path!)); restored.Show();
            await restored.RestoreAfterUpdateAsync(state);
            PdfPageViewState? actual;
            if (state.Mode == PdfDisplayMode.View)
            {
                var viewer = Field<PdfView>(restored, "viewer");
                await WaitUntilAsync(() => Field<bool>(viewer, "ready") && Field<Task>(viewer, "restoringView").IsCompleted);
                actual = await viewer.CapturePageViewAsync();
            }
            else if (state.Mode == PdfDisplayMode.Pages) actual = Field<PdfEditor>(restored, "editor").Viewer.CapturePageView();
            else
            {
                var canvas = Field<PdfCommentEditor>(restored, "commentEditor").Viewer; actual = canvas.CapturePageView();
                Check(canvas.Selection?.Caret == state.Caret && canvas.Selection?.Anchor == state.Anchor);
            }
            var expected = state.View!;
            if (actual is null || actual.Page != expected.Page || Math.Abs(actual.Width - expected.Width) > 3 || Math.Abs(actual.CenterX - expected.CenterX) * expected.Width > 5 || Math.Abs(actual.CenterY - expected.CenterY) * expected.Width > 5)
                throw new Exception($"Session restoration moved viewport: {state.Mode}, expected {expected}, actual {actual}");
            host.Controls.Remove(restored);
        }
    });
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;
    private static void Check(bool condition) { if (!condition) throw new Exception("Update session assertion failed."); }
    private static void Throws(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Expected invalid session rejection"); }
}
