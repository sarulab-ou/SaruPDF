using System.Reflection;
using System.Text;
using Microsoft.Web.WebView2.WinForms;
using PdfSharp.Pdf.IO;
using PdfSharp.Pdf.Annotations;
using SaruPdf.Documents;
using SaruPdf.Views;
using static SaruPdf.Tests.PdfEditingTests;

namespace SaruPdf.Tests;

internal static class PdfCopyTests
{
    internal static IEnumerable<(string Name, Action Run)> Cases(string root)
    {
        string Fixture(string name)
        {
            string path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".pdf");
            SamplePdf.Write(path, name); return path;
        }
        yield return ("cross-PDF multi-copy survives source disposal and supports repeated paste", () =>
        {
            string source = Fixture("Source"), target = Fixture("Target");
            byte[] original = File.ReadAllBytes(source);
            PdfPageBundle bundle;
            using (var input = new PdfEditSession(source))
            {
                bundle = input.Copy([2, 0, 2]);
                Check(bundle.PageCount == 2 && !input.IsDirty && !input.CanUndo);
                Check(File.ReadAllBytes(source).SequenceEqual(original));
            }
            File.Delete(source);
            using var output = new PdfEditSession(target);
            output.PasteCopy(1, bundle); Check(output.PageCount == 5 && output.IsDirty);
            output.Save(target);
            Content(target, "Target:1", "Source:1", "Source:3", "Target:2", "Target:3");
            output.Undo(); Check(output.PageCount == 3 && output.IsDirty);
            output.Redo(); Check(output.PageCount == 5 && !output.IsDirty);
            output.PasteCopy(0, bundle); output.Save(target);
            Content(target, "Source:1", "Source:3", "Target:1", "Source:1", "Source:3", "Target:2", "Target:3");
        });
        yield return ("copied imported pages can be moved and recopied into a third PDF", () =>
        {
            string a = Fixture("A"), b = Fixture("B"), c = Fixture("C");
            using var source = new PdfEditSession(a);
            using var middle = new PdfEditSession(b);
            middle.PasteCopy(3, source.Copy([1, 2]));
            var combined = middle.Copy([2, 3, 4]);
            middle.Cut([3, 4]); middle.Paste(1); middle.Save(b);
            Content(b, "B:1", "A:2", "A:3", "B:2", "B:3");
            using var last = new PdfEditSession(c); last.PasteCopy(3, combined); last.Save(c);
            Content(c, "C:1", "C:2", "C:3", "B:3", "A:2", "A:3");
        });
        yield return ("same-PDF copy creates independent duplicates without removing the source", () =>
        {
            string path = Fixture("Same");
            using var session = new PdfEditSession(path);
            var bundle = session.Copy([0]);
            Check(!session.IsDirty && session.PageCount == 3);
            session.PasteCopy(3, bundle); session.PasteCopy(4, bundle);
            session.Cut(3); session.Paste(0); session.Save(path);
            Content(path, "Same:1", "Same:1", "Same:2", "Same:3", "Same:1");
        });
        yield return ("foreign pages preserve dimensions, rotation and notes without replacing destination metadata", () =>
        {
            string source = Fixture("Annotated"), target = Fixture("Destination");
            using (var pdf = PdfReader.Open(source, PdfDocumentOpenMode.Modify))
            {
                pdf.Info.Title = "Source title";
                pdf.Pages[0].Rotate = 90;
                pdf.Pages[0].Width = PdfSharp.Drawing.XUnit.FromPoint(444);
                pdf.Pages[0].Annotations.Add(new PdfTextAnnotation { Contents = "Copied note" }); pdf.Save(source);
            }
            using (var pdf = PdfReader.Open(target, PdfDocumentOpenMode.Modify))
            {
                pdf.Info.Title = "Destination title";
                pdf.Outlines.Add("Destination bookmark", pdf.Pages[0], true); pdf.Save(target);
            }
            using var input = new PdfEditSession(source);
            using var output = new PdfEditSession(target);
            output.PasteCopy(0, input.Copy([0, 2])); output.Save(target);
            Content(target, "Annotated:1", "Annotated:3", "Destination:1", "Destination:2", "Destination:3");
            using var saved = PdfReader.Open(target, PdfDocumentOpenMode.Modify);
            Check(saved.Info.Title == "Destination title" && saved.Pages[0].Rotate == 90);
            Check(Math.Abs(saved.Pages[0].Width.Point - 444) < .01);
            Check(saved.Pages[0].Annotations.Count == 1 && saved.Pages[0].Annotations[0].Contents == "Copied note");
            Check(saved.Outlines[0].DestinationPage!.Reference!.ObjectID == saved.Pages[2].Reference!.ObjectID);
        });
        yield return ("cross-PDF copy retains embedded image resources and pixel data", () =>
        {
            string source = Fixture("Image source"), target = Fixture("Image target");
            using (var pdf = PdfReader.Open(source, PdfDocumentOpenMode.Modify))
            using (var bitmap = new Bitmap(24, 24))
            using (var pixels = Graphics.FromImage(bitmap))
            using (var stream = new MemoryStream())
            {
                pixels.Clear(Color.Lime); bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png); stream.Position = 0;
                using var image = PdfSharp.Drawing.XImage.FromStream(stream);
                using (var graphics = PdfSharp.Drawing.XGraphics.FromPdfPage(pdf.Pages[1])) graphics.DrawImage(image, 20, 20, 120, 120);
                pdf.Save(source);
            }
            static byte[] ImageBytes(string path, int page)
            {
                using var pdf = PdfReader.Open(path, PdfDocumentOpenMode.Modify);
                var images = pdf.Pages[page].Elements.GetDictionary("/Resources")!.Elements.GetDictionary("/XObject")!;
                var image = images.Elements.KeyNames.Select(key => images.Elements.GetDictionary(key.Value)!)
                    .Single(item => item.Elements.GetName("/Subtype") == "/Image");
                Check(image.Elements.GetInteger("/Width") == 24 && image.Elements.GetInteger("/Height") == 24);
                return image.Stream.UnfilteredValue;
            }
            byte[] expected = ImageBytes(source, 1);
            using var input = new PdfEditSession(source);
            using var output = new PdfEditSession(target); output.PasteCopy(0, input.Copy([1])); output.Save(target);
            Check(ImageBytes(target, 0).SequenceEqual(expected));
        });
        yield return ("invalid copy and local cut conflicts preserve both documents and undo history", () =>
        {
            using var source = new PdfEditSession(Fixture("Source"));
            Throws<ArgumentOutOfRangeException>(() => source.Copy([]));
            Throws<ArgumentOutOfRangeException>(() => source.Copy([0, 3]));
            Check(!source.IsDirty && !source.CanUndo);
            var bundle = source.Copy([0, 2]);
            using var target = new PdfEditSession(Fixture("Target"));
            Throws<ArgumentOutOfRangeException>(() => target.PasteCopy(4, bundle));
            Check(!target.CanUndo && target.PageCount == 3);
            target.Cut(1);
            Throws<InvalidOperationException>(() => target.PasteCopy(0, bundle));
            Throws<InvalidOperationException>(() => target.Copy([0]));
            Check(target.HasPendingCut && target.PageCount == 2);
            target.Paste(0); target.Save(target.SourcePath); Content(target.SourcePath, "Target:2", "Target:1", "Target:3");
        });
        yield return ("failed cross-PDF save keeps original bytes and imported pages for retry", () =>
        {
            string target = Fixture("Target"); byte[] original = File.ReadAllBytes(target);
            using var source = new PdfEditSession(Fixture("Source"));
            using var session = new PdfEditSession(target); session.PasteCopy(2, source.Copy([0, 1]));
            using (var locked = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
                Throws<IOException>(() => session.Save(target));
            Check(session.IsDirty && session.PageCount == 5 && File.ReadAllBytes(target).SequenceEqual(original));
            session.Save(target); Content(target, "Target:1", "Target:2", "Source:1", "Source:2", "Target:3");
        });
    }

    internal static Task VerifyWindowsAsync(string root) => OnSurfaceAsync(async host =>
    {
        string sourcePath = Path.Combine(root, "copy-source-colors.pdf"), targetPath = Path.Combine(root, "copy-target-colors.pdf");
        PageNavigationTests.WriteColorPdf(sourcePath); PageNavigationTests.WriteColorPdf(targetPath);
        using (var landscape = PdfReader.Open(sourcePath, PdfDocumentOpenMode.Modify))
        {
            landscape.Pages[1].Width = PdfSharp.Drawing.XUnit.FromPoint(842);
            landscape.Pages[1].Height = PdfSharp.Drawing.XUnit.FromPoint(595);
            landscape.Save(sourcePath);
        }
        byte[] originalSource = File.ReadAllBytes(sourcePath);
        var clipboard = new PdfPageClipboard();
        using var source = new PdfWindow(Path.Combine(root, "copy-source-profile-" + Guid.NewGuid().ToString("N")), clipboard) { TopLevel = false, Dock = DockStyle.Fill };
        using var target = new PdfWindow(Path.Combine(root, "copy-target-profile-" + Guid.NewGuid().ToString("N")), clipboard) { TopLevel = false, Dock = DockStyle.Fill };
        host.Controls.Add(source); host.Controls.Add(target);
        source.SetDocument(DocumentPath.Open(sourcePath)); target.SetDocument(DocumentPath.Open(targetPath));
        source.Show(); target.Show(); source.ToggleEditMode(); target.ToggleEditMode();
        var input = Editor(source); var output = Editor(target);
        Exception? failure = null; input.Failed += ex => failure = ex; output.Failed += ex => failure = ex;
        target.BringToFront(); await ExpectCanvasColorAsync(output.Viewer, 0, Color.Red); Check(!output.CanPaste);
        source.BringToFront(); await ExpectCanvasColorAsync(input.Viewer, 0, Color.Red);
        input.SelectPage(2); RevealPage(input.Viewer, 2);
        input.Viewer.SelectAt(VisibleCenter(input.Viewer, 2), Keys.Control);
        Check(input.Viewer.SelectedPages.SequenceEqual([1, 2]));
        Key(source, Keys.Control | Keys.C);
        await WaitUntilAsync(() => clipboard.Current?.PageCount == 2);
        Check(!input.Session.IsDirty && File.ReadAllBytes(sourcePath).SequenceEqual(originalSource));
        Check(output.CanPaste && Menu(target, "pastePage").Enabled);
        var firstCopy = clipboard.Current;
        using (var menu = input.Viewer.CreatePageMenu(VisibleCenter(input.Viewer, 2)))
            menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Name == "CopyPages").PerformClick();
        Check(!ReferenceEquals(firstCopy, clipboard.Current) && clipboard.Current?.PageCount == 2);
        source.Close(); Check(source.IsDisposed);
        target.BringToFront();
        using (var menu = output.Viewer.CreatePageMenu(output.Viewer.GapPoint(0)))
            menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Name == "PasteAt0").PerformClick();
        Check(output.Session.PageCount == 14 && output.Viewer.SelectedPages.SequenceEqual([0, 1]));
        await ExpectCanvasColorAsync(output.Viewer, 0, Color.Lime);
        output.SelectPage(2); await ExpectCanvasColorAsync(output.Viewer, 1, Color.Blue);
        output.SelectEnd(); Key(target, Keys.Control | Keys.V);
        await WaitUntilAsync(() => output.Session.PageCount == 16);
        output.SelectPage(16); await ExpectCanvasColorAsync(output.Viewer, 15, Color.Blue);
        output.SelectPage(15); await ExpectCanvasColorAsync(output.Viewer, 14, Color.Lime);
        output.Undo(); Check(output.Session.PageCount == 14); output.Redo(); Check(output.Session.PageCount == 16);
        Key(target, Keys.Control | Keys.S); await WaitUntilAsync(() => !output.Session.IsDirty);
        using (var saved = PdfReader.Open(targetPath, PdfDocumentOpenMode.Import)) Check(saved.PageCount == 16);
        Check(File.ReadAllBytes(sourcePath).SequenceEqual(originalSource) && failure is null);
        clipboard.Clear(); Check(!output.CanPaste && !Menu(target, "pastePage").Enabled);
        target.ToggleEditMode();
        var readViewer = (PdfView)typeof(PdfWindow).GetField("viewer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
        var web = (WebView2)typeof(PdfView).GetField("web", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(readViewer)!;
        await WaitUntilAsync(() => web.CoreWebView2 is not null && web.Source?.IsFile == true);
        await PageNavigationTests.ExpectColorAsync(web, Color.Lime);
        web.CoreWebView2.Navigate(new UriBuilder(DocumentPath.Open(targetPath).Uri) { Query = "verify-copy", Fragment = "toolbar=0&page=16" }.Uri.AbsoluteUri);
        await PageNavigationTests.ExpectColorAsync(web, Color.Blue);
        target.Close(); Check(target.IsDisposed);
    });

    private static PdfEditor Editor(PdfWindow window) => (PdfEditor)typeof(PdfWindow).GetField("editor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static ToolStripMenuItem Menu(PdfWindow window, string field) => (ToolStripMenuItem)typeof(PdfWindow).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static void Key(PdfWindow window, Keys keys)
    {
        var method = typeof(PdfWindow).GetMethod("ProcessCmdKey", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Check((bool)method.Invoke(window, [new Message(), keys])!);
    }
    private static void Content(string path, params string[] expected)
    {
        using var pdf = PdfReader.Open(path, PdfDocumentOpenMode.Modify); Check(pdf.PageCount == expected.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            string[] marker = expected[i].Split(':');
            string content = Encoding.ASCII.GetString(pdf.Pages[i].Contents.CreateSingleContent().Stream.UnfilteredValue);
            if (!content.Contains($"{marker[0]} - page {marker[1]}", StringComparison.Ordinal))
                throw new Exception($"Copied page {i + 1} does not contain {expected[i]}.");
        }
    }
    private static void Check(bool condition) { if (!condition) throw new Exception("PDF copy assertion failed."); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
}
