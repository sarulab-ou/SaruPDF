using System.Reflection;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Annotations;
using PdfSharp.Pdf.IO;
using SaruPdf.Documents;
using SaruPdf.Views;
using static SaruPdf.Tests.PdfEditingTests;

namespace SaruPdf.Tests;

internal static class PdfExternalCommentTests
{
    private sealed class Annotation(PdfSharp.Pdf.PdfDocument document) : PdfAnnotation(document);
    private static string Fixture(string root)
    {
        string path = Path.Combine(root, "external-comments-" + Guid.NewGuid().ToString("N") + ".pdf");
        SamplePdf.Write(path, "External annotations");
        using var pdf = PdfReader.Open(path, PdfDocumentOpenMode.Modify);
        void Add(int page, string text, int flags = 4)
        {
            var note = new Annotation(pdf);
            note.Elements.SetName("/Subtype", "/Highlight"); note.Elements.SetString("/Contents", text);
            note.Elements.SetString("/T", "Another reviewer"); note.Elements.SetString("/NM", "external-" + page + "-" + flags);
            note.Elements.SetInteger("/F", flags);
            var quad = new PdfArray(pdf); foreach (double n in new double[] { 55, 670, 300, 670, 55, 650, 300, 650 }) quad.Elements.Add(new PdfReal(n));
            note.Elements["/QuadPoints"] = quad;
            note.Elements.SetRectangle("/Rect", new PdfRectangle(new PdfSharp.Drawing.XPoint(55, 650), new PdfSharp.Drawing.XPoint(300, 670)));
            pdf.Pages[page].Annotations.Add(note);
            var popup = new Annotation(pdf); popup.Elements.SetName("/Subtype", "/Popup");
            popup.Elements.SetString("/Contents", text); popup.Elements["/Parent"] = note.Reference!;
            pdf.Pages[page].Annotations.Add(popup); note.Elements["/Popup"] = popup.Reference!;
        }
        Add(0, "External review note"); Add(1, "日本語の校正コメント"); Add(2, "Hidden note", 2);
        pdf.Save(path); return path;
    }
    internal static void VerifyPreservation(string root)
    {
        string path = Fixture(root); var original = File.ReadAllBytes(path);
        var labels = PdfExternalCommentLabels.Read(path);
        try
        {
            Check(labels.Count == 2 && labels[0].Text == "External review note" && labels[1].Text == "日本語の校正コメント", "External contents or popup deduplication failed");
            Check(labels.All(label => label.Label.Outline.PointCount > 0) && PdfCommentLabel.FontSize == 7, "External labels must use the managed label layout");
        }
        finally { foreach (var label in labels) label.Dispose(); }
        using var session = new PdfEditSession(path);
        Check(session.Comments.Count == 0 && !session.IsDirty, "Reading another author's notes must not create edits");
        session.Rotate([0], 90); string output = Path.Combine(root, "external-comments-preserved.pdf"); session.Save(output);
        using var saved = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        var notes = saved.Pages.Cast<PdfPage>().SelectMany(page => page.Annotations.Cast<PdfAnnotation>()).Where(a => a.Elements.GetName("/Subtype") == "/Highlight").ToArray();
        Check(notes.Length == 3 && notes.All(a => a.Elements.GetString("/T") == "Another reviewer" && a.Elements.GetString("/NM").StartsWith("external-")), "Author or IDs changed during save");
        Check(notes.Any(a => a.Elements.GetString("/Contents") == "日本語の校正コメント") && !saved.Pages.Cast<PdfPage>().Any(p => p.Annotations.Cast<PdfAnnotation>().Any(a => a.Elements.GetName("/Subtype") == "/FreeText")), "Foreign comments were converted or lost");
        Check(File.ReadAllBytes(path).SequenceEqual(original), "Original PDF changed");
    }
    internal static Task VerifyUiAsync(string root) => VerifyFileAsync(Fixture(root), root, 2);
    internal static Task VerifyFileAsync(string path, string root, int expectedCount) => OnSurfaceAsync(async host =>
    {
        var original = File.ReadAllBytes(path);
        var labels = PdfExternalCommentLabels.Read(path);
        Check(labels.Count == expectedCount, $"Expected {expectedCount} external comments, got {labels.Count}");
        var first = labels[0]; var normalized = first.Label.NormalizedBounds;
        foreach (var label in labels) label.Dispose();
        using var window = new PdfWindow(Path.Combine(root, "external-profile-" + Guid.NewGuid().ToString("N"))) { TopLevel = false, Dock = DockStyle.Fill };
        host.Controls.Add(window); window.SetDocument(DocumentPath.Open(path)); window.Show();
        var viewer = Field<PdfView>(window, "viewer"); var web = Field<WebView2>(viewer, "web");
        await WaitUntilAsync(() => Field<bool>(viewer, "ready")); await Field<Task>(viewer, "initializingExternalComments");
        var count = await PdfViewportBridge.EvaluateAsync(web.CoreWebView2, "document.querySelectorAll('[data-sarupdf-external-comments] path').length");
        Check(count?.GetInt32() == expectedCount, "View mode did not overlay all external labels");
        await PdfViewportBridge.EvaluateAsync(web.CoreWebView2, $$"""
            (()=>{const v=window.viewer.viewport_, r=v.getPageScreenRect({{first.Page}});
                v.scrollTo({x:v.position.x,y:v.position.y+r.y+r.height*{{normalized.Top.ToString(System.Globalization.CultureInfo.InvariantCulture)}}-v.size.height/2});return true;})()
            """);
        await Task.Delay(150);
        var rect = await PdfViewportBridge.EvaluateAsync(web.CoreWebView2, $"window.viewer.viewport_.getPageScreenRect({first.Page})");
        using (var stream = new MemoryStream())
        {
            await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
            stream.Position = 0; using var bitmap = new Bitmap(stream);
            bitmap.Save(Path.Combine(root, "external-comments-view.png"));
            var r = rect!.Value;
            var page = new Rectangle((int)r.GetProperty("x").GetDouble(), (int)r.GetProperty("y").GetDouble(), (int)r.GetProperty("width").GetDouble(), (int)r.GetProperty("height").GetDouble());
            HasRed(bitmap, page, normalized);
        }
        window.ToggleEditMode(); var editor = Field<PdfEditor>(window, "editor");
        await WaitUntilAsync(() => editor.Visible && editor.Viewer.IsPageRendered(first.Page));
        Capture(editor.Viewer, "external-comments-pages");
        window.ToggleCommentMode(); var comments = Field<PdfCommentEditor>(window, "commentEditor");
        await WaitUntilAsync(() => comments.Visible && comments.Viewer.IsPageRendered(first.Page));
        Capture(comments.Viewer, "external-comments-comments");
        Check(comments.Session.Comments.Count == 0 && !comments.Session.IsDirty, "Displaying foreign comments must not make them editable or dirty");
        Check(File.ReadAllBytes(path).SequenceEqual(original), "Displaying comments changed original PDF bytes");
        void Capture(PdfPageCanvas canvas, string name)
        {
            using var bitmap = new Bitmap(canvas.Width, canvas.Height); canvas.DrawToBitmap(bitmap, canvas.ClientRectangle);
            bitmap.Save(Path.Combine(root, name + ".png")); HasRed(bitmap, canvas.PageBounds(first.Page), normalized);
        }
    });
    private static void HasRed(Bitmap bitmap, Rectangle page, RectangleF normalized)
    {
        var target = Rectangle.Intersect(new Rectangle(0, 0, bitmap.Width, bitmap.Height), Rectangle.Ceiling(new RectangleF(page.X + page.Width*normalized.X - 3, page.Y + page.Height*normalized.Y - 3, page.Width*normalized.Width+6, page.Height*normalized.Height+6)));
        int red = 0;
        for (int y = target.Top; y < target.Bottom; y++) for (int x = target.Left; x < target.Right; x++)
        { var c = bitmap.GetPixel(x, y); if (c.R > 180 && c.G < 100 && c.B < 100) red++; }
        Check(red > 8, $"External label is not visibly red: {red} pixels at {target}");
    }
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
