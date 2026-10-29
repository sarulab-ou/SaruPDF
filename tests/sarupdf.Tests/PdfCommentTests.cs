using System.Reflection;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SaruPdf.Documents;
using SaruPdf.Views;
using static SaruPdf.Tests.PdfEditingTests;
using static SaruPdf.Tests.PdfCopyTests;

namespace SaruPdf.Tests;

internal static class PdfCommentTests
{
    internal static IEnumerable<(string Name, Action Run)> Cases(string root)
    {
        string Fixture(string name)
        {
            string path = Path.Combine(root, "comment-" + Guid.NewGuid().ToString("N") + ".pdf");
            SamplePdf.Write(path, name); return path;
        }
        yield return ("text map extracts glyphs, word spaces, lines and multiple pages", () =>
        {
            var map = PdfTextMap.Read(Fixture("Comment fixture"));
            var selection = new PdfCaretSelection(map); selection.SelectAll();
            Check(map.Pages.Count == 3 && map.Glyphs.Count > 100);
            Check(selection.SelectedText.Contains("Comment fixture - page 1"));
            Check(selection.SelectedText.Contains("SaruPDF acceptance test / local PDF rendering"));
            Check(selection.Regions().Count == 3 && map.Glyphs.All(g => float.IsFinite(g.Bounds.Left) && float.IsFinite(g.Bounds.Top)));
        });
        yield return ("two-column selections continue down the same column across paragraphs", () =>
        {
            string path = Path.Combine(root, "comment-two-columns.pdf"); SamplePdf.WriteTwoColumns(path);
            var map = PdfTextMap.Read(path);
            int Find(string value)
            {
                for (int i = 0; i < map.Glyphs.Count; i++)
                    if (map.Glyphs[i].Text == value[..1] && string.Concat(map.Glyphs.Skip(i).Take(value.Length).Select(item => item.Text)) == value) return i;
                return -1;
            }
            int leftStart = Find("Left paragraph line 1"), leftEnd = Find("Left paragraph line 8"), rightStart = Find("Right paragraph line 1"), rightEnd = Find("Right paragraph line 8");
            Check(leftStart >= 0 && leftStart < leftEnd && leftEnd < rightStart && rightStart < rightEnd);
            var selection = new PdfCaretSelection(map); selection.Set(leftStart, false); selection.Set(leftEnd + 4, true);
            Check(selection.SelectedText.Contains("Left paragraph line 5") && !selection.SelectedText.Contains("Right paragraph"));
            Check(selection.Regions().Single().Quads.All(q => map.Pages[0].Rectangle(q).Right < .5));
            selection.Set(rightStart, false); selection.Set(rightEnd + 5, true);
            Check(selection.SelectedText.Contains("Right paragraph line 5") && !selection.SelectedText.Contains("Left paragraph"));
            selection.Set(Find("Left paragraph line 4"), false); selection.Move(Keys.Down, true);
            Check(selection.Current is { } current && current.Bounds.Left < .5 && selection.SelectedText.Contains("Left paragraph line 4"));
        });
        yield return ("narrow columns with merged lines keep selection and highlights in the intended column", () =>
        {
            string path = Path.Combine(root, "comment-narrow-columns.pdf"); SamplePdf.WriteNarrowColumns(path);
            using (var pdf = UglyToad.PdfPig.PdfDocument.Open(path))
            {
                var words = pdf.GetPage(1).GetWords(UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor.NearestNeighbourWordExtractor.Instance);
                var raw = UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter.DocstrumBoundingBoxes.Instance.GetBlocks(words);
                Check(raw.Any(block => block.TextLines.Any(line => line.Words.Any(w => w.Text.StartsWith("Left")) && line.Words.Any(w => w.Text.StartsWith("Right")))));
            }
            var map = PdfTextMap.Read(path);
            var lines = map.Glyphs.Select((glyph, index) => (glyph, index)).GroupBy(item => item.glyph.Line)
                .Select(line => (Index: line.First().index, Text: string.Concat(line.Select(item => item.glyph.Text)))).ToArray();
            int Find(string text) => lines.Single(line => line.Text.StartsWith(text)).Index;
            int left1 = Find("Left01"), left10 = Find("Left10"), right1 = Find("Right01"), right10 = Find("Right10");
            Check(left1 < left10 && left10 < right1 && right1 < right10);
            var selection = new PdfCaretSelection(map);
            void CheckLeft(int start, int end)
            {
                selection.Set(start, false); selection.Set(end, true);
                Check(selection.SelectedText.Contains("Left05") && !selection.SelectedText.Contains("Right"));
                Check(selection.Regions().Single().Quads.All(q => map.Pages[0].Rectangle(q).Right < .5));
            }
            CheckLeft(left1, left10 + 5); CheckLeft(left10 + 5, left1);
            selection.Set(right1, false); selection.Set(right10 + 6, true);
            Check(selection.SelectedText.Contains("Right05") && !selection.SelectedText.Contains("Left"));
            selection.Set(left1, false); selection.Set(right10 + 6, true);
            Check(selection.SelectedText.Contains("Left10") && selection.SelectedText.Contains("Right01"));
            selection.Set(left1, false); selection.Move(Keys.Down, true);
            Check(selection.Current is { } current && current.Bounds.Left < .5f && selection.SelectedText.Contains("Left01"));
            selection.SelectAll(); Check(selection.SelectedText.Contains("Left10") && selection.SelectedText.Contains("Right10"));
        });
        yield return ("Japanese CJK text supports glyph selection and Unicode PDF comments", () =>
        {
            string path = Path.Combine(root, "japanese-caret.pdf"); SamplePdf.WriteJapanese(path);
            var map = PdfTextMap.Read(path); var selection = new PdfCaretSelection(map);
            selection.Set(3, true); Check(selection.SelectedText == "日本語");
            using var session = new PdfEditSession(path); session.AddComment(selection.Regions(), "日本語の範囲にコメント");
            session.Save(Path.Combine(root, "japanese-caret-comment.pdf"));
            VerifyStandard(session.SourcePath, 1);
            Check(PdfTextMap.Read(session.SourcePath).ExistingComments.All(c => c.Text == "日本語の範囲にコメント"));
        });
        yield return ("image-only PDFs have no fabricated text or selectable comment regions", () =>
        {
            string path = Path.Combine(root, "comment-image-only.pdf"); PageNavigationTests.WriteColorPdf(path);
            var selection = new PdfCaretSelection(PdfTextMap.Read(path));
            selection.SelectAll(); selection.Move(Keys.Down, true);
            Check(selection.Map.Pages.Count == 12 && selection.Map.Glyphs.Count == 0 && selection.Regions().Count == 0);
        });
        yield return ("caret arrows, line Home/End, document selection and reverse Shift preserve the anchor", () =>
        {
            var selection = new PdfCaretSelection(PdfTextMap.Read(Fixture("Caret")));
            selection.Move(Keys.Right, true); selection.Move(Keys.Right, true); selection.Move(Keys.Right, true);
            Check(selection.SelectedText == "Car");
            selection.Move(Keys.Left, true); Check(selection.SelectedText == "Ca" && selection.Anchor == 0);
            selection.Move(Keys.End, false); Check(selection.Current?.Text == "\n");
            selection.Move(Keys.Home, false); Check(selection.Caret == 0);
            selection.Move(Keys.Down, false); Check(selection.Current?.Line == 1);
            selection.Move(Keys.Up, false); Check(selection.Current?.Line == 0);
            selection.Move(Keys.End, true, true); Check(selection.Regions().Count == 3);
            selection.Move(Keys.Home, false, true); Check(!selection.HasSelection && selection.Caret == 0);
            selection.Move(Keys.PageDown, false); Check(selection.Current?.Page == 1);
            selection.Move(Keys.PageUp, false); Check(selection.Current?.Page == 0);
            var empty = new PdfCaretSelection(new PdfTextMap()); empty.Move(Keys.Right, true); Check(!empty.HasSelection);
        });
        yield return ("Unicode comments save as standard Highlight/FreeText without a speech-bubble marker", () =>
        {
            string original = Fixture("Standard comment"), saved = Path.Combine(root, "standard-comments.pdf");
            byte[] before = File.ReadAllBytes(original);
            var selection = new PdfCaretSelection(PdfTextMap.Read(original)); selection.Set(12, true);
            using var session = new PdfEditSession(original);
            session.AddComment(selection.Regions(), "日本語のコメント\n2行目です 🐵"); session.Save(saved);
            Check(!session.IsDirty && File.ReadAllBytes(original).SequenceEqual(before));
            VerifyStandard(saved, 1);
            session.Save(saved); VerifyStandard(saved, 1);
            session.Undo(); Check(session.IsDirty && session.Comments.Count == 0);
            session.Redo(); Check(!session.IsDirty && session.Comments.Count == 1);
            var read = PdfTextMap.Read(saved);
            Check(read.ExistingComments.Count == 1 && read.ExistingComments.All(c => c.Text == "日本語のコメント\r2行目です 🐵"));
        });
        yield return ("multi-page comments undo atomically and retain their page after a page move", () =>
        {
            string original = Fixture("Multi comment"), saved = Path.Combine(root, "multi-comments-" + Guid.NewGuid().ToString("N") + ".pdf");
            var selection = new PdfCaretSelection(PdfTextMap.Read(original)); selection.SelectAll();
            using var session = new PdfEditSession(original);
            session.AddComment(selection.Regions(), "Across pages"); Check(session.Comments.Count == 3);
            session.Undo(); Check(!session.IsDirty && session.Comments.Count == 0);
            session.Redo(); session.Cut(0); session.Paste(2); session.Save(saved); VerifyStandard(saved, 3);
            using var pdf = PdfReader.Open(saved, PdfDocumentOpenMode.Import);
            Check(pdf.Pages.Cast<PdfPage>().All(page => page.Annotations.Count == 2));
        });
        yield return ("invalid comments and failed saves preserve PDF bytes, comments and undo state", () =>
        {
            string path = Fixture("Protected"), target = Fixture("Locked");
            byte[] before = File.ReadAllBytes(target);
            var selection = new PdfCaretSelection(PdfTextMap.Read(path)); selection.Set(4, true);
            using var session = new PdfEditSession(path);
            Throws<ArgumentException>(() => session.AddComment(selection.Regions(), " "));
            Throws<ArgumentException>(() => session.AddComment([new(99, selection.Regions()[0].Quads)], "bad"));
            Check(!session.CanUndo && !session.IsDirty);
            session.AddComment(selection.Regions(), "Keep this");
            using (var held = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
                Throws<IOException>(() => session.Save(target));
            Check(session.IsDirty && session.Comments.Count == 1 && File.ReadAllBytes(target).SequenceEqual(before));
            File.AppendAllText(path, "\n% external edit\n");
            Throws<IOException>(() => session.Save(path)); session.Save(target); VerifyStandard(target, 1);
        });
        yield return ("rotated and cropped glyph selections produce quads in original PDF user space", () =>
        {
            string path = Fixture("Rotation");
            using (var edit = PdfReader.Open(path, PdfDocumentOpenMode.Modify))
            {
                edit.Pages[0].Rotate = 90;
                edit.Pages[0].CropBox = new PdfRectangle(new PdfSharp.Drawing.XPoint(40, 50), new PdfSharp.Drawing.XPoint(555, 800));
                edit.Pages[1].Rotate = 180; edit.Pages[2].Rotate = 270; edit.Save(path);
            }
            var selection = new PdfCaretSelection(PdfTextMap.Read(path)); selection.Set(1, true);
            var quad = selection.Regions()[0].Quads[0];
            Check(quad.Points.All(point => point.X >= 60 && point.X < 100 && point.Y > 700 && point.Y < 800));
            using var session = new PdfEditSession(path); session.AddComment(selection.Regions(), "Rotation check");
            session.Save(Path.Combine(root, "rotated-comment.pdf"));
        });
        yield return ("inline comment labels overlap the first highlight and stay within cropped page edges", () =>
        {
            foreach (int rotation in new[] { 0, 90, 180, 270 })
            {
                var page = new PdfTextPage(rotation is 90 or 270 ? 750 : 515, rotation is 90 or 270 ? 515 : 750, 40, 50, 515, 750, rotation);
                var highlight = new RectangleF(.72f, .55f, .12f, .025f);
                using var label = new PdfCommentLabel(page, [page.Quad(highlight)], "日本語の赤字コメント\n2行目も赤色です");
                Check(label.Outline.PointCount > 20 && label.NormalizedBounds.IntersectsWith(highlight));
                Check(label.NormalizedBounds.Left >= 0 && label.NormalizedBounds.Top >= 0 && label.NormalizedBounds.Right <= 1 && label.NormalizedBounds.Bottom <= 1);
                Check(Math.Abs(label.Bounds.Left + label.Outline.GetBounds().Left - highlight.Left * page.Width) < .01);
                var wrapTarget = new RectangleF(.8f, .5f, .1f, .02f);
                using var wrapped = new PdfCommentLabel(page, [page.Quad(wrapTarget)], "English and 日本語 comment that must wrap before the page edge. English and 日本語.");
                Check(wrapped.Outline.PointCount > 20 && Math.Abs(wrapped.Bounds.Left + wrapped.Outline.GetBounds().Left - wrapTarget.Left * page.Width) < .01);
                Check(wrapped.Outline.GetBounds().Height > PdfCommentLabel.FontSize * 2 && wrapped.Bounds.Left + wrapped.Outline.GetBounds().Right <= page.Width + .01);
                var edge = new RectangleF(.99f, .002f, .009f, .025f);
                using var longLabel = new PdfCommentLabel(page, [page.Quad(edge)], string.Concat(Enumerable.Repeat("長いコメントです。", 300)));
                Check(longLabel.NormalizedBounds.Left >= 0 && longLabel.NormalizedBounds.Top >= 0 && longLabel.NormalizedBounds.Right <= 1 && longLabel.NormalizedBounds.Bottom <= 1);
            }
        });
        yield return ("inline comment appearances preserve the original page boxes and body text", () =>
        {
            string source = Fixture("Preserve page"), saved = Path.Combine(root, "red-comment-preserved.pdf");
            var before = PdfTextMap.Read(source);
            var selection = new PdfCaretSelection(before); selection.Set(3, true);
            using var session = new PdfEditSession(source); session.AddComment(selection.Regions(), "赤字コメント"); session.Save(saved);
            using var pdf = PdfReader.Open(saved, PdfDocumentOpenMode.Import);
            Check(pdf.Pages[0].MediaBoxReadOnly.Width == 595 && pdf.Pages[0].MediaBoxReadOnly.Height == 842);
            Check(!pdf.Pages[0].HasCropBox);
            var after = PdfTextMap.Read(saved);
            Check(after.Pages.SequenceEqual(before.Pages) && string.Concat(after.Glyphs.Select(g => g.Text)) == string.Concat(before.Glyphs.Select(g => g.Text)));
        });
        yield return ("Latin and mixed-language labels always have visible glyph outlines", () =>
        {
            var page = new PdfTextPage(595, 842, 0, 0, 595, 842, 0);
            foreach (string text in new[] { "This paper is accepted by SenSys 2023.", "A", "English line one\nEnglish line two", "日本語 and English" })
            {
                using var label = new PdfCommentLabel(page, [page.Quad(new RectangleF(.15f, .3f, .2f, .02f))], text);
                Check(label.Outline.PointCount > 10);
            }
        });
        yield return ("saved comments can be edited, undone, redone and resaved without duplication", () =>
        {
            string source = Fixture("Editable"), saved = Path.Combine(root, "editable-comments.pdf");
            var selection = new PdfCaretSelection(PdfTextMap.Read(source)); selection.Set(4, true);
            using (var first = new PdfEditSession(source)) { first.AddComment(selection.Regions(), "Original English"); first.Save(saved); }
            using var session = new PdfEditSession(saved);
            Check(session.Comments.Count == 1 && !session.IsDirty);
            var comment = session.Comments.Single(); session.UpdateComment(comment.Id, "Changed 日本語 and English");
            Check(session.IsDirty && session.Comments.Single().Id == comment.Id);
            session.Undo(); Check(!session.IsDirty && session.Comments.Single().Text == "Original English");
            session.Redo(); session.Save(saved); VerifyStandard(saved, 1);
            session.Save(saved); VerifyStandard(saved, 1);
            using var reopened = new PdfEditSession(saved);
            Check(reopened.Comments.Single().Text == "Changed 日本語 and English" && !reopened.IsDirty);
            reopened.UpdateComment(comment.Id, "Changed 日本語 and English"); Check(!reopened.IsDirty && !reopened.CanUndo);
            Throws<ArgumentException>(() => reopened.UpdateComment(comment.Id, " "));
        });
    }

    internal static Task VerifyUiAsync(string root) => OnSurfaceAsync(async host =>
    {
        using (var existing = new PdfCommentDialog("注釈", "1行目\r2行目"))
        {
            var text = existing.Controls.Find("CommentText", true).OfType<TextBox>().Single();
            Check(text.ReadOnly && text.Text == "1行目" + Environment.NewLine + "2行目");
        }
        string path = Path.Combine(root, "comment-ui-source.pdf"); SamplePdf.Write(path, "Comment UI");
        using var window = new PdfWindow(Path.Combine(root, "comment-ui-profile-" + Guid.NewGuid().ToString("N"))) { TopLevel = false, Dock = DockStyle.Fill };
        host.Controls.Add(window); window.SetDocument(DocumentPath.Open(path)); window.Show();
        var menu = window.MainMenuStrip!.Items.OfType<ToolStripMenuItem>().Single(m => m.Text == "編集(&E)")
            .DropDownItems.OfType<ToolStripMenuItem>().Single(m => m.Name == "CommentMode");
        menu.PerformClick(); Check(menu.Checked);
        var editor = (PdfCommentEditor)typeof(PdfWindow).GetField("commentEditor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        await WaitUntilAsync(() => editor.Viewer.Visible && editor.Viewer.Selection is not null && editor.Viewer.IsPageRendered(0));
        var selection = editor.Viewer.Selection!;
        int body = selection.Map.Glyphs.FindIndex(g => g.Page == 0 && g.Bounds.Top > .15f && g.Text == "S");
        Check(body >= 0); selection.Set(body, false);
        Key(window, Keys.Shift | Keys.Right); Key(window, Keys.Shift | Keys.Right);
        Check(selection.SelectedText == "Sa");
        using (var bitmap = new Bitmap(editor.Viewer.Width, editor.Viewer.Height))
        {
            editor.Viewer.DrawToBitmap(bitmap, editor.Viewer.ClientRectangle);
            bitmap.Save(Path.Combine(root, "comment-caret-selection.png"));
        }
        bool sawDialog = false, cancelFirst = true;
        using var timer = new System.Windows.Forms.Timer { Interval = 80 };
        timer.Tick += (_, _) =>
        {
            var dialog = Application.OpenForms.OfType<PdfCommentDialog>().FirstOrDefault();
            if (dialog is null) return;
            timer.Stop(); sawDialog = true;
            var add = dialog.Controls.Find("AddComment", true).OfType<Button>().Single(); Check(!add.Enabled);
            if (cancelFirst) { dialog.DialogResult = DialogResult.Cancel; dialog.Close(); return; }
            dialog.Controls.Find("CommentText", true).OfType<TextBox>().Single().Text = "日本語の標準コメント";
            Check(add.Enabled); add.PerformClick();
        };
        timer.Start(); Key(window, Keys.Enter);
        await WaitUntilAsync(() => sawDialog && !Application.OpenForms.OfType<PdfCommentDialog>().Any());
        Check(!editor.Session.IsDirty && editor.Session.Comments.Count == 0 && selection.HasSelection);
        sawDialog = false; cancelFirst = false; timer.Start(); Key(window, Keys.Enter);
        await WaitUntilAsync(() => editor.Session.Comments.Count == 1);
        Check(sawDialog && editor.Session.IsDirty && !selection.HasSelection);
        using (var pending = new Bitmap(editor.Viewer.Width, editor.Viewer.Height))
        {
            editor.Viewer.DrawToBitmap(pending, editor.Viewer.ClientRectangle);
            Check(RedPixels(pending) > 20);
            pending.Save(Path.Combine(root, "comment-inline-unsaved.png"));
        }
        Key(window, Keys.Control | Keys.Z); await WaitUntilAsync(() => editor.Session.Comments.Count == 0);
        Key(window, Keys.Control | Keys.Y); await WaitUntilAsync(() => editor.Session.Comments.Count == 1);
        string saved = Path.Combine(root, "comment-ui-saved.pdf"); Check(window.SaveToPath(saved)); VerifyStandard(saved, 1);
        Check(!editor.Session.IsDirty);
        window.ToggleCommentMode(); Check(!menu.Checked);
        window.ToggleEditMode(); Check(!menu.Checked && PdfCopyTests.Editor(window) is not null);
        window.ToggleEditMode();
        using var rendered = new PdfPageCanvas { ShowPageSelection = false }; host.Controls.Add(rendered); rendered.BringToFront();
        rendered.SetOrder([0, 1, 2], 0); await rendered.LoadAsync(saved);
        var glyph = selection.Map.Glyphs[body]; rendered.CenterPointOnPage(0, glyph.Bounds.Top + glyph.Bounds.Height / 2);
        await WaitUntilAsync(() => rendered.IsPageRendered(0));
        using var image = new Bitmap(rendered.Width, rendered.Height); rendered.DrawToBitmap(image, rendered.ClientRectangle);
        image.Save(Path.Combine(root, "comment-saved-render.png"));
        Check(RedPixels(image) > 20);
        int yellow = 0;
        for (int y = 0; y < image.Height; y++) for (int x = 0; x < image.Width; x++)
        { var pixel = image.GetPixel(x, y); if (IsYellow(pixel)) yellow++; }
        Check(yellow > 20);
        var redBounds = PixelBounds(image, color => color.R > 180 && color.G < 80 && color.B < 80);
        var yellowBounds = PixelBounds(image, IsYellow, Rectangle.Inflate(redBounds, 24, 48));
        Check(redBounds.Top < yellowBounds.Top && redBounds.Bottom > yellowBounds.Top);
    });

    private static void VerifyStandard(string path, int expected)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        int highlights = 0, popups = 0, labels = 0;
        foreach (PdfPage page in document.Pages)
        for (int i = 0; i < page.Annotations.Count; i++)
        {
            var annotation = page.Annotations[i]; string type = annotation.Elements.GetName("/Subtype");
            if (type == "/Popup") { popups++; Check(annotation.Elements.GetReference("/Parent") is not null); continue; }
            if (type == "/FreeText")
            {
                labels++;
                Check(annotation.Elements.GetString("/Contents").Length > 0 && annotation.Elements.GetString("/DA") == "1 0 0 rg /Helv 10 Tf");
                Check(annotation.Elements.GetName("/RT") == "/Group" && annotation.Elements.GetReference("/IRT") is not null);
                Check(annotation.Elements.GetDictionary("/AP")?.Elements.GetDictionary("/N")?.Stream is not null);
                continue;
            }
            if (type != "/Highlight") continue;
            highlights++;
            Check(annotation.Elements.GetString("/Contents").Length == 0 && annotation.Elements.GetArray("/QuadPoints")!.Elements.Count % 8 == 0);
            Check(annotation.Elements.GetDictionary("/AP")?.Elements.GetDictionary("/N")?.Stream is not null);
            Check(annotation.Elements.GetReference("/Popup") is null && annotation.Elements.GetReference("/P") is not null);
        }
        Check(highlights == expected && popups == 0 && labels == expected);
    }

    internal static Task VerifyBrowserAsync(string root) => OnSurfaceAsync(async host =>
    {
        foreach (string text in new[] { "This paper is accepted by SenSys 2023.", "日本語のコメント" })
        {
            string name = text.StartsWith("This", StringComparison.Ordinal) ? "english" : "japanese";
            string source = Path.Combine(root, name + "-browser-source.pdf"), saved = Path.Combine(root, name + "-browser-saved.pdf");
            SamplePdf.Write(source, "Browser comment");
            var selection = new PdfCaretSelection(PdfTextMap.Read(source)); selection.Set(7, true);
            using (var session = new PdfEditSession(source)) { session.AddComment(selection.Regions(), text); session.Save(saved); }
            using var viewer = new PdfView(DocumentPath.Open(saved), Path.Combine(root, name + "-browser-profile-" + Guid.NewGuid().ToString("N"))) { Dock = DockStyle.Fill };
            host.Controls.Add(viewer); viewer.BringToFront(); await viewer.LoadAsync();
            var web = (WebView2)typeof(PdfView).GetField("web", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewer)!;
            bool visible = false;
            for (int attempt = 0; attempt < 40; attempt++)
            {
                await Task.Delay(150);
                using var data = new MemoryStream();
                await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, data); data.Position = 0;
                using var bitmap = new Bitmap(data);
                if (RedPixels(bitmap) > 20) { bitmap.Save(Path.Combine(root, name + "-browser-red.png")); visible = true; break; }
            }
            Check(visible); host.Controls.Remove(viewer);
        }
    });

    internal static Task VerifyNarrowColumnDragAsync(string root) => OnSurfaceAsync(async host =>
    {
        string source = Path.Combine(root, "narrow-column-drag.pdf"); SamplePdf.WriteNarrowColumns(source);
        using var window = new PdfWindow(Path.Combine(root, "narrow-profile-" + Guid.NewGuid().ToString("N"))) { TopLevel = false, Dock = DockStyle.Fill };
        host.Controls.Add(window); window.SetDocument(DocumentPath.Open(source)); window.Show();
        var edit = window.MainMenuStrip!.Items.OfType<ToolStripMenuItem>().Single(m => m.Text == "編集(&E)");
        edit.DropDownItems.OfType<ToolStripMenuItem>().Single(m => m.Name == "CommentMode").PerformClick();
        var editor = (PdfCommentEditor)typeof(PdfWindow).GetField("commentEditor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var canvas = editor.Viewer;
        await WaitUntilAsync(() => canvas.Visible && canvas.Selection is not null && canvas.IsPageRendered(0));
        var selection = canvas.Selection!;
        var lines = selection.Map.Glyphs.Select((g, i) => (g, i)).GroupBy(x => x.g.Line)
            .Select(line => (Index: line.First().i, Text: string.Concat(line.Select(x => x.g.Text)))).ToArray();
        int Find(string prefix) => lines.Single(line => line.Text.StartsWith(prefix)).Index;
        int left1 = Find("Left01"), left8 = Find("Left08"), right1 = Find("Right01"), right8 = Find("Right08");
        canvas.CenterPointOnPage(0, selection.Map.Glyphs[left1].Bounds.Top + .05f);
        Point At(int index)
        {
            var glyph = selection.Map.Glyphs[index]; var rect = canvas.PageBounds(glyph.Page);
            return new((int)(rect.Left + rect.Width * (glyph.Bounds.Left + glyph.Bounds.Width * .2f)),
                (int)(rect.Top + rect.Height * (glyph.Bounds.Top + glyph.Bounds.Height * .5f)));
        }
        void Drag(int from, int to)
        { canvas.BeginTextDrag(At(from), Keys.None); canvas.ContinueTextDrag(At(to)); canvas.EndTextDrag(At(to)); }
        Drag(left1, left8); Check(selection.SelectedText.Contains("Left05") && !selection.SelectedText.Contains("Right"));
        Check(selection.Regions().Single().Quads.All(q => selection.Map.Pages[0].Rectangle(q).Right < .5));
        Drag(left8, left1); Check(selection.SelectedText.Contains("Left05") && !selection.SelectedText.Contains("Right"));
        Drag(right1, right8); Check(selection.SelectedText.Contains("Right05") && !selection.SelectedText.Contains("Left"));
        Drag(left1, right8); Check(selection.SelectedText.Contains("Left08") && selection.SelectedText.Contains("Right05"));
        host.Controls.Remove(window);
    });

    internal static Task VerifyTwoColumnDragAsync(string root) => OnSurfaceAsync(async host =>
    {
        string source = Path.Combine(root, "two-column-drag.pdf"); SamplePdf.WriteTwoColumns(source);
        using var window = new PdfWindow(Path.Combine(root, "column-profile-" + Guid.NewGuid().ToString("N"))) { TopLevel = false, Dock = DockStyle.Fill };
        host.Controls.Add(window); window.SetDocument(DocumentPath.Open(source)); window.Show();
        var edit = window.MainMenuStrip!.Items.OfType<ToolStripMenuItem>().Single(m => m.Text == "編集(&E)");
        edit.DropDownItems.OfType<ToolStripMenuItem>().Single(m => m.Name == "CommentMode").PerformClick();
        var editor = (PdfCommentEditor)typeof(PdfWindow).GetField("commentEditor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var canvas = editor.Viewer;
        await WaitUntilAsync(() => canvas.Visible && canvas.Selection is not null && canvas.IsPageRendered(0));
        var selection = canvas.Selection!;
        var leftLines = selection.Map.Glyphs.Select((g, i) => (g, i)).Where(x => x.g.Text == "L" && x.g.Bounds.Left < .5f).ToArray();
        Check(leftLines.Length == 8);
        canvas.CenterPointOnPage(0, leftLines[3].g.Bounds.Top);
        Point At(int index)
        {
            var glyph = selection.Map.Glyphs[index]; var rect = canvas.PageBounds(glyph.Page);
            return new((int)(rect.Left + rect.Width * (glyph.Bounds.Left + glyph.Bounds.Width * .2f)),
                (int)(rect.Top + rect.Height * (glyph.Bounds.Top + glyph.Bounds.Height * .5f)));
        }
        canvas.BeginTextDrag(At(leftLines[0].i), Keys.None); canvas.ContinueTextDrag(At(leftLines[6].i)); canvas.EndTextDrag(At(leftLines[6].i));
        Check(selection.SelectedText.Contains("Left paragraph line 5") && !selection.SelectedText.Contains("Right paragraph"));
        Check(selection.Regions().Single().Quads.All(q => selection.Map.Pages[0].Rectangle(q).Right < .5));
        host.Controls.Remove(window);
    });

    internal static Task VerifyDragEditModesAsync(string root) => OnSurfaceAsync(async host =>
    {
        string source = Path.Combine(root, "drag-edit-source.pdf"), saved = Path.Combine(root, "drag-edit-saved.pdf"); SamplePdf.Write(source, "Drag selection");
        using var window = new PdfWindow(Path.Combine(root, "drag-edit-profile-" + Guid.NewGuid().ToString("N"))) { TopLevel = false, Dock = DockStyle.Fill };
        host.Controls.Add(window); window.SetDocument(DocumentPath.Open(source)); window.Show();
        var modes = window.MainMenuStrip!.Items.OfType<ToolStripMenuItem>().Single(m => m.Text == "編集(&E)").DropDownItems.OfType<ToolStripMenuItem>().ToArray();
        var view = modes.Single(m => m.Name == "ViewMode"); var comments = modes.Single(m => m.Name == "CommentMode"); var pages = modes.Single(m => m.Name == "EditMode");
        Check(view.Checked && !comments.Checked && !pages.Checked);
        comments.PerformClick(); comments.PerformClick(); Check(comments.Checked && !view.Checked);
        var editor = (PdfCommentEditor)typeof(PdfWindow).GetField("commentEditor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var canvas = editor.Viewer;
        await WaitUntilAsync(() => canvas.Visible && canvas.Selection is not null && canvas.IsPageRendered(0));
        var selection = canvas.Selection!;
        int start = selection.Map.Glyphs.FindIndex(g => g.Page == 0 && g.Bounds.Top > .15f && g.Text == "S");
        var first = selection.Map.Glyphs[start]; canvas.CenterPointOnPage(0, first.Bounds.Top + first.Bounds.Height / 2);
        Point PointAt(int index, bool after)
        {
            var glyph = selection.Map.Glyphs[index]; var rect = canvas.PageBounds(glyph.Page);
            return new Point((int)(rect.Left + rect.Width * (after ? glyph.Bounds.Right - .0001f : glyph.Bounds.Left + .0001f)),
                (int)(rect.Top + rect.Height * (glyph.Bounds.Top + glyph.Bounds.Height / 2)));
        }
        canvas.BeginTextDrag(PointAt(start, false), Keys.None); canvas.ContinueTextDrag(PointAt(start + 6, true)); canvas.EndTextDrag(PointAt(start + 6, true));
        Check(selection.SelectedText == "SaruPDF");
        bool requested = false; canvas.CommentRequested += () => requested = true;
        using (var menu = canvas.CreateCommentMenu(PointAt(start, false))) menu.Items.OfType<ToolStripMenuItem>().Single(m => m.Name == "AddComment").PerformClick();
        // Close the actual add dialog without changing the document.
        bool cancelled = false;
        using var timer = new System.Windows.Forms.Timer { Interval = 50 };
        timer.Tick += (_, _) => { var dialog = Application.OpenForms.OfType<PdfCommentDialog>().FirstOrDefault(); if (dialog is null) return; timer.Stop(); cancelled = true; dialog.DialogResult = DialogResult.Cancel; dialog.Close(); };
        timer.Start(); await WaitUntilAsync(() => requested && cancelled && !Application.OpenForms.OfType<PdfCommentDialog>().Any());
        Check(selection.SelectedText == "SaruPDF" && !editor.Session.IsDirty);
        editor.AddComment("Before edit"); Check(window.SaveToPath(saved));
        view.PerformClick(); Check(view.Checked && !comments.Checked);
        comments.PerformClick();
        editor = (PdfCommentEditor)typeof(PdfWindow).GetField("commentEditor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        await WaitUntilAsync(() => editor.Viewer.Visible && editor.Viewer.Selection is not null);
        Check(editor.Session.Comments.Count == 1 && !editor.Session.IsDirty);
        var current = editor.Session.Comments.Single();
        var reloadedCanvas = editor.Viewer;
        var highlight = reloadedCanvas.Selection!.Map.Pages[current.PageId].Rectangle(current.Quads[0]);
        reloadedCanvas.CenterPointOnPage(current.PageId, highlight.Top + highlight.Height / 2);
        var pageRect = reloadedCanvas.PageBounds(current.PageId);
        var hit = new Point((int)(pageRect.Left + pageRect.Width * (highlight.Left + highlight.Width / 2)),
            (int)(pageRect.Top + pageRect.Height * (highlight.Top + highlight.Height / 2)));
        using var editTimer = new System.Windows.Forms.Timer { Interval = 50 };
        editTimer.Tick += (_, _) =>
        {
            var dialog = Application.OpenForms.OfType<PdfCommentDialog>().FirstOrDefault(); if (dialog is null) return;
            var input = dialog.Controls.Find("CommentText", true).OfType<TextBox>().Single();
            Check(!input.ReadOnly); input.Text = "After edit 日本語";
            editTimer.Stop(); dialog.Controls.Find("SaveComment", true).OfType<Button>().Single().PerformClick();
        };
        editTimer.Start();
        using (var menu = reloadedCanvas.CreateCommentMenu(hit)) menu.Items.OfType<ToolStripMenuItem>().Single(m => m.Name == "EditComment").PerformClick();
        await WaitUntilAsync(() => editor.Session.IsDirty && editor.Session.Comments.Single().Text == "After edit 日本語");
        Check(window.SaveToPath(saved));
        view.PerformClick(); pages.PerformClick(); Check(pages.Checked && !view.Checked && !comments.Checked);
        pages.PerformClick(); Check(pages.Checked); view.PerformClick(); Check(view.Checked);
        using var reopened = new PdfEditSession(saved); Check(reopened.Comments.Single().Text == "After edit 日本語"); VerifyStandard(saved, 1);
    });
    private static int RedPixels(Bitmap image)
    {
        int red = 0;
        for (int y = 0; y < image.Height; y++) for (int x = 0; x < image.Width; x++)
        { var pixel = image.GetPixel(x, y); if (pixel.R > 180 && pixel.G < 80 && pixel.B < 80) red++; }
        return red;
    }
    private static bool IsYellow(Color color) => color.R > 230 && color.G > 200 && color.B < color.G - 40;
    private static Rectangle PixelBounds(Bitmap image, Func<Color, bool> matches, Rectangle? area = null)
    {
        int left = image.Width, top = image.Height, right = -1, bottom = -1;
        var region = Rectangle.Intersect(area ?? new Rectangle(0, 0, image.Width, image.Height), new Rectangle(0, 0, image.Width, image.Height));
        for (int y = region.Top; y < region.Bottom; y++) for (int x = region.Left; x < region.Right; x++)
            if (matches(image.GetPixel(x, y))) { left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y); }
        return right < left ? Rectangle.Empty : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
    }
    private static void Check(bool condition) { if (!condition) throw new Exception("PDF comment assertion failed."); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
}
