using System.Reflection;
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
        yield return ("Unicode comments save as standard Highlight/Popup with quads, appearance and parent links", () =>
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
            Check(read.ExistingComments.Count == 2 && read.ExistingComments.All(c => c.Text == "日本語のコメント\r2行目です 🐵"));
        });
        yield return ("multi-page comments undo atomically and retain their page after a page move", () =>
        {
            string original = Fixture("Multi comment"), saved = Path.Combine(root, "multi-comments.pdf");
            var selection = new PdfCaretSelection(PdfTextMap.Read(original)); selection.SelectAll();
            using var session = new PdfEditSession(original);
            session.AddComment(selection.Regions(), "Across pages"); Check(session.Comments.Count == 3);
            session.Undo(); Check(!session.IsDirty && session.Comments.Count == 0);
            session.Redo(); session.Cut(0); session.Paste(2); session.Save(saved); VerifyStandard(saved, 3);
            using var pdf = PdfReader.Open(saved, PdfDocumentOpenMode.Import);
            Check(pdf.Pages.Cast<PdfPage>().All(page => page.Annotations.Count == 3));
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
                Check(annotation.Elements.GetString("/Contents").Length > 0 && annotation.Elements.GetString("/DA").Contains("1 0 0 rg"));
                Check(annotation.Elements.GetName("/RT") == "/Group" && annotation.Elements.GetReference("/IRT") is not null);
                Check(annotation.Elements.GetDictionary("/AP")?.Elements.GetDictionary("/N")?.Stream is not null);
                continue;
            }
            if (type != "/Highlight") continue;
            highlights++;
            Check(annotation.Elements.GetString("/Contents").Length > 0 && annotation.Elements.GetArray("/QuadPoints")!.Elements.Count % 8 == 0);
            Check(annotation.Elements.GetDictionary("/AP")?.Elements.GetDictionary("/N")?.Stream is not null);
            Check(annotation.Elements.GetReference("/Popup") is not null && annotation.Elements.GetReference("/P") is not null);
        }
        Check(highlights == expected && popups == expected && labels == expected);
    }
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
