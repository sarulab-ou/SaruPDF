using System.Reflection;
using System.Text;
using Microsoft.Web.WebView2.WinForms;
using PdfSharp.Pdf.Annotations;
using PdfSharp.Pdf.IO;
using SaruPdf.Documents;
using SaruPdf.Views;

namespace SaruPdf.Tests;

internal static class PdfEditingTests
{
    internal static IEnumerable<(string Name, Action Run)> Cases(string root)
    {
        string Fixture()
        {
            string path = Path.Combine(root, "edit-" + Guid.NewGuid().ToString("N") + ".pdf");
            SamplePdf.Write(path, "Edit fixture"); return path;
        }
        yield return ("cut and paste moves one page to start, middle and end without loss", () =>
        {
            string path = Fixture(); byte[] original = File.ReadAllBytes(path);
            using var session = new PdfEditSession(path);
            session.Cut(2); session.Paste(0); Order(session, 2, 0, 1);
            session.Cut(0); session.Paste(1); Order(session, 0, 2, 1);
            session.Cut(1); session.Paste(session.PageCount); Order(session, 0, 1, 2);
            Check(!session.IsDirty);
            session.Cut(0); session.Paste(session.PageCount); Order(session, 1, 2, 0);
            Check(File.ReadAllBytes(path).SequenceEqual(original));
            string output = Path.Combine(root, "並べ替え 保存 #100% &.pdf");
            session.Save(output);
            AssertContent(output, 2, 3, 1);
            Check(!session.IsDirty && session.SourcePath == output);
            Check(File.ReadAllBytes(path).SequenceEqual(original));
        });
        yield return ("cut remains recoverable and cannot be saved until pasted", () =>
        {
            string path = Fixture(); byte[] original = File.ReadAllBytes(path);
            using var session = new PdfEditSession(path);
            session.Cut(1); Check(session.HasPendingCut && session.IsDirty && session.PageCount == 2);
            Throws<InvalidOperationException>(() => session.Cut(0));
            Throws<InvalidOperationException>(() => session.Save(path));
            Check(File.ReadAllBytes(path).SequenceEqual(original));
            Check(session.Undo()); Order(session, 0, 1, 2); Check(!session.IsDirty && !session.HasPendingCut);
            Check(session.Redo()); Check(session.CutPage == 1);
            session.Paste(1); Check(!session.IsDirty);
        });
        yield return ("undo and redo preserve cut buffer and saved-state tracking", () =>
        {
            using var session = new PdfEditSession(Fixture());
            session.Cut(0); session.Paste(2);
            session.Save(session.SourcePath); Check(!session.IsDirty);
            Check(session.Undo()); Check(session.HasPendingCut && session.IsDirty);
            Check(session.Undo()); Order(session, 0, 1, 2); Check(session.IsDirty);
            Check(session.Redo()); Check(session.Redo()); Check(!session.IsDirty);
            session.Undo(); session.Undo(); session.Cut(2); Check(!session.CanRedo);
        });
        yield return ("single-page PDF supports cut, empty staging and paste without deletion", () =>
        {
            string path = Path.Combine(root, "single-edit.pdf");
            using (var pdf = new PdfSharp.Pdf.PdfDocument()) { pdf.AddPage(); pdf.Save(path); }
            using var session = new PdfEditSession(path);
            session.Cut(0); Check(session.PageCount == 0 && session.WritePreview() is null);
            Throws<InvalidOperationException>(() => session.Save(path));
            session.Paste(0); session.Save(path);
            using var saved = PdfReader.Open(path, PdfDocumentOpenMode.Import);
            Check(saved.PageCount == 1 && !session.IsDirty);
        });
        yield return ("failed save preserves destination, dirty state and removes staging file", () =>
        {
            string path = Fixture(); byte[] original = File.ReadAllBytes(path);
            using var session = new PdfEditSession(path);
            session.Cut(0); session.Paste(2);
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                Throws<IOException>(() => session.Save(path));
            Check(File.ReadAllBytes(path).SequenceEqual(original) && session.IsDirty);
            Check(!Directory.EnumerateFiles(root, ".SaruPDF-*.tmp").Any());
            session.Save(path); AssertContent(path, 2, 3, 1); Check(!session.IsDirty);
        });
        yield return ("external modification blocks overwrite but allows save as", () =>
        {
            string path = Fixture();
            using var session = new PdfEditSession(path);
            session.Cut(0); session.Paste(2);
            SamplePdf.Write(path, "Externally changed"); byte[] external = File.ReadAllBytes(path);
            Throws<IOException>(() => session.Save(path));
            Check(File.ReadAllBytes(path).SequenceEqual(external) && session.IsDirty);
            string copy = Path.Combine(root, "edit-external-copy.pdf"); session.Save(copy);
            AssertContent(copy, 2, 3, 1);
            session.Cut(2); session.Paste(0); session.Save(copy); AssertContent(copy, 1, 2, 3);
            Check(File.ReadAllBytes(path).SequenceEqual(external));
        });
        yield return ("reordering preserves metadata, rotation, annotations and bookmark target", () =>
        {
            string path = Fixture();
            using (var pdf = PdfReader.Open(path, PdfDocumentOpenMode.Modify))
            {
                pdf.Info.Title = "Original metadata";
                pdf.Pages[0].Rotate = 90;
                pdf.Pages[0].Annotations.Add(new PdfTextAnnotation { Contents = "Original note" });
                pdf.Outlines.Add("First page bookmark", pdf.Pages[0], true);
                pdf.Save(path);
            }
            using var session = new PdfEditSession(path); session.Cut(0); session.Paste(2); session.Save(path);
            using var saved = PdfReader.Open(path, PdfDocumentOpenMode.Modify);
            Check(saved.Info.Title == "Original metadata" && saved.Pages[2].Rotate == 90);
            Check(saved.Pages[2].Annotations.Count == 1 && saved.Pages[2].Annotations[0].Contents == "Original note");
            Check(saved.Outlines.Count == 1 && saved.Outlines[0].DestinationPage!.Reference!.ObjectID == saved.Pages[2].Reference!.ObjectID);
            AssertContent(path, 2, 3, 1);
        });
        yield return ("invalid page positions do not damage edit history", () =>
        {
            using var session = new PdfEditSession(Fixture());
            Throws<ArgumentOutOfRangeException>(() => session.Cut(-1));
            Throws<ArgumentOutOfRangeException>(() => session.Cut(3)); Check(!session.CanUndo);
            session.Cut(1); Throws<ArgumentOutOfRangeException>(() => session.Paste(3));
            Check(session.HasPendingCut); session.Paste(0); Order(session, 1, 0, 2);
        });
        yield return ("password-protected and signature PDFs are rejected without changing input", () =>
        {
            string protectedPath = Path.Combine(root, "protected-edit.pdf");
            using (var pdf = new PdfSharp.Pdf.PdfDocument())
            {
                pdf.AddPage(); pdf.SecuritySettings.UserPassword = "fixture-password"; pdf.Save(protectedPath);
            }
            byte[] protectedBytes = File.ReadAllBytes(protectedPath);
            Throws<PdfReaderException>(() => { using var session = new PdfEditSession(protectedPath); });
            Check(File.ReadAllBytes(protectedPath).SequenceEqual(protectedBytes));
            string signaturePath = Fixture();
            using (var pdf = PdfReader.Open(signaturePath, PdfDocumentOpenMode.Modify))
            {
                // A signature field, not a fabricated valid cryptographic signature.
                var field = new PdfSharp.Pdf.PdfDictionary(pdf);
                field.Elements.SetName("/FT", "/Sig");
                pdf.Internals.AddObject(field);
                pdf.Internals.Catalog.Elements["/SaruPDFTestSignatureField"] = field.Reference!;
                pdf.Save(signaturePath);
            }
            byte[] signatureBytes = File.ReadAllBytes(signaturePath);
            Throws<InvalidDataException>(() => { using var session = new PdfEditSession(signaturePath); });
            Check(File.ReadAllBytes(signaturePath).SequenceEqual(signatureBytes));
        });
        yield return ("multiple cut pages retain PDF order through paste, undo, redo and save", () =>
        {
            string path = Fixture();
            using var session = new PdfEditSession(path);
            session.Cut([2, 0, 2]); Check(session.CutPages.SequenceEqual([0, 2])); Order(session, 1);
            string preview = session.WritePreview()!;
            AssertContent(preview, 2);
            Throws<InvalidOperationException>(() => session.Save(path));
            session.Paste(1); Order(session, 1, 0, 2);
            session.Save(path); AssertContent(path, 2, 1, 3);
            session.Undo(); Check(session.CutPages.Count == 2); session.Undo(); Order(session, 0, 1, 2);
            session.Redo(); session.Redo(); Check(!session.IsDirty);
            session.Cut([0, 1, 2]); Check(session.PageCount == 0 && session.WritePreview() is null);
            session.Paste(0); session.Save(path); AssertContent(path, 2, 1, 3);
        });
        yield return ("invalid multi-page selections leave state and history intact", () =>
        {
            using var session = new PdfEditSession(Fixture());
            Throws<ArgumentOutOfRangeException>(() => session.Cut([]));
            Throws<ArgumentOutOfRangeException>(() => session.Cut([0, 3]));
            Check(!session.IsDirty && !session.CanUndo); Order(session, 0, 1, 2);
        });
        yield return ("save-as catalog changes retain one-window-per-document behavior", () =>
        {
            var catalog = new WindowCatalog<object>(); var a = new object(); var b = new object();
            catalog.Add(a); catalog.Assign("A.pdf", a); catalog.Add(b); catalog.Assign("B.pdf", b);
            catalog.Reassign("C.pdf", a);
            Check(!catalog.TryFind("A.pdf", out _) && catalog.TryFind("C.pdf", out var current) && ReferenceEquals(a, current));
            Throws<InvalidOperationException>(() => catalog.Reassign("B.pdf", a));
            Check(catalog.TryFind("C.pdf", out _) && catalog.TryFind("B.pdf", out current) && ReferenceEquals(b, current));
        });
    }

    internal static Task VerifyEditorRenderingAsync(string root) => OnSurfaceAsync(async host =>
    {
        string pdf = Path.Combine(root, "editor-colors.pdf");
        PageNavigationTests.WriteColorPdf(pdf);
        using var editor = new PdfEditor(new PdfEditSession(pdf));
        host.Controls.Add(editor);
        Exception? renderingError = null;
        editor.Failed += ex => renderingError = ex;
        await editor.LoadAsync();
        var canvas = editor.Viewer;
        await ExpectCanvasColorAsync(canvas, 0, Color.Red);
        Check(editor.Controls.Count == 1 && editor.Controls[0] is PdfPageCanvas);
        Check(!editor.Controls.OfType<SplitContainer>().Any());
        canvas.SelectAt(VisibleCenter(canvas, 0), Keys.None);
        RevealPage(canvas, 2);
        canvas.SelectAt(VisibleCenter(canvas, 2), Keys.Control);
        Check(canvas.SelectedPages.SequenceEqual([0, 2]));
        canvas.SelectAt(VisibleCenter(canvas, 2), Keys.Control);
        Check(canvas.SelectedPages.SequenceEqual([0]));
        RevealPage(canvas, 4);
        canvas.SelectAt(VisibleCenter(canvas, 4), Keys.Shift);
        Check(canvas.SelectedPages.SequenceEqual([2, 3, 4]));
        canvas.SelectPage(1);
        RevealPage(canvas, 2);
        canvas.SelectAt(VisibleCenter(canvas, 2), Keys.Control);
        using (var menu = canvas.CreatePageMenu(VisibleCenter(canvas, 2)))
            menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Name == "CutPages").PerformClick();
        Check(editor.Session.CutPages.SequenceEqual([0, 2]) && editor.Session.PageCount == 10);
        await ExpectCanvasColorAsync(canvas, 0, Color.Lime);
        var gap = canvas.GapPoint(1);
        canvas.AutoScrollPosition = new Point(0, Math.Max(0, gap.Y - canvas.AutoScrollPosition.Y - 100));
        using (var menu = canvas.CreatePageMenu(canvas.GapPoint(1)))
            menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Name == "PasteAt1").PerformClick();
        Check(!editor.Session.HasPendingCut && editor.Session.PageCount == 12);
        Check(editor.Session.Pages.Take(4).SequenceEqual([1, 0, 2, 3]));
        Check(canvas.SelectedPages.SequenceEqual([1, 2]));
        await ExpectCanvasColorAsync(canvas, 1, Color.Red);
        Check(canvas.TryHandlePageKey(Keys.NumPad3)); Check(canvas.TryHandlePageKey(Keys.Enter));
        await ExpectCanvasColorAsync(canvas, 2, Color.Blue);
        string savedPath = Path.Combine(root, "editor-render-saved.pdf");
        editor.Session.Save(savedPath); editor.UpdateState();
        editor.Undo(); Check(editor.Session.CutPages.SequenceEqual([0, 2]));
        editor.Undo(); Check(!editor.Session.HasPendingCut && editor.Session.IsDirty);
        canvas.SelectPage(1); await ExpectCanvasColorAsync(canvas, 0, Color.Red);
        editor.Redo(); editor.Redo(); Check(!editor.Session.IsDirty);
        canvas.SelectAll(); editor.Cut(); Check(editor.Session.PageCount == 0 && editor.Session.CutPages.Count == 12);
        using (var menu = canvas.CreatePageMenu(new Point(100, 100)))
            menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Name == "PasteAt0").PerformClick();
        Check(editor.Session.PageCount == 12 && !editor.Session.IsDirty);
        using var savedViewer = new PdfPageCanvas();
        host.Controls.Add(savedViewer); savedViewer.BringToFront();
        savedViewer.SetOrder(Enumerable.Range(0, 12), 0);
        await savedViewer.LoadAsync(savedPath);
        await ExpectCanvasColorAsync(savedViewer, 0, Color.Lime);
        savedViewer.SelectPage(2); await ExpectCanvasColorAsync(savedViewer, 1, Color.Red);
        savedViewer.SelectPage(3); await ExpectCanvasColorAsync(savedViewer, 2, Color.Blue);
        host.ClientSize = new Size(860, 650);
        savedViewer.SelectPage(3); await ExpectCanvasColorAsync(savedViewer, 2, Color.Blue);
        using (var menu = savedViewer.CreatePageMenu(VisibleCenter(savedViewer, 2)))
            menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "縮小").PerformClick();
        savedViewer.SelectPage(3); await ExpectCanvasColorAsync(savedViewer, 2, Color.Blue);
        Check(renderingError is null);
        using var snapshot = new Bitmap(savedViewer.Width, savedViewer.Height);
        savedViewer.DrawToBitmap(snapshot, savedViewer.ClientRectangle);
        snapshot.Save(Path.Combine(root, "direct-edit-render.png"));
        string sample = Path.Combine(root, "direct-edit-layout.pdf");
        SamplePdf.Write(sample, "Direct page editing");
        using var layout = new PdfPageCanvas();
        host.Controls.Add(layout); layout.BringToFront(); layout.SetOrder([0, 1, 2], 0);
        await layout.LoadAsync(sample);
        await WaitUntilAsync(() => layout.IsPageRendered(0));
        using var layoutImage = new Bitmap(layout.Width, layout.Height);
        layout.DrawToBitmap(layoutImage, layout.ClientRectangle);
        layoutImage.Save(Path.Combine(root, "direct-edit-layout.png"));
    });

    internal static Task VerifyWindowCommandsAsync(string root) => OnSurfaceAsync(async host =>
    {
        string pdf = Path.Combine(root, "window-edit-colors.pdf");
        PageNavigationTests.WriteColorPdf(pdf);
        using var window = new PdfWindow(Path.Combine(root, "window-edit-profile-" + Guid.NewGuid().ToString("N"))) { TopLevel = false, Dock = DockStyle.Fill };
        host.Controls.Add(window);
        var editMode = window.MainMenuStrip!.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "編集(&E)").DropDownItems.OfType<ToolStripMenuItem>().Single(item => item.Name == "EditMode");
        Check(!editMode.Enabled);
        window.SetDocument(DocumentPath.Open(pdf)); Check(editMode.Enabled);
        window.Show(); editMode.PerformClick(); Check(editMode.Checked);
        var editor = (PdfEditor)typeof(PdfWindow).GetField("editor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        await ExpectCanvasColorAsync(editor.Viewer, 0, Color.Red);
        var address = window.Controls.Find("AddressBar", true).OfType<TextBox>().Single();
        Check(address.Text == pdf && address.ReadOnly && address.Parent!.Bottom <= editor.Top + editor.Parent!.Top);
        bool Key(Keys keys)
        {
            var method = typeof(PdfWindow).GetMethod("ProcessCmdKey", BindingFlags.Instance | BindingFlags.NonPublic)!;
            object[] args = [new Message(), keys]; return (bool)method.Invoke(window, args)!;
        }
        Check(address.Focus() && Key(Keys.Control | Keys.A) && address.SelectionLength == pdf.Length);
        Check(!editor.Session.HasPendingCut);
        editor.Viewer.Focus();
        Check(Key(Keys.Control | Keys.X));
        await WaitUntilAsync(() => editor.Session.HasPendingCut);
        await ExpectCanvasColorAsync(editor.Viewer, 0, Color.Lime);
        editor.SelectEnd(); Check(Key(Keys.Control | Keys.V));
        await WaitUntilAsync(() => !editor.Session.HasPendingCut);
        await ExpectCanvasColorAsync(editor.Viewer, 11, Color.Red);
        Check(Key(Keys.Control | Keys.S));
        await WaitUntilAsync(() => !editor.Session.IsDirty);
        using (var check = PdfReader.Open(pdf, PdfDocumentOpenMode.Import)) Check(check.PageCount == 12);
        string savedAs = Path.Combine(root, "address-save-" + Guid.NewGuid().ToString("N") + ".pdf");
        Check(window.SaveToPath(savedAs) && address.Text == savedAs);
        window.MainMenuStrip!.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "編集(&E)")
            .DropDownItems.OfType<ToolStripMenuItem>().Single(item => item.Name == "ViewMode").PerformClick(); Check(!editMode.Checked);
        var viewer = (PdfView)typeof(PdfWindow).GetField("viewer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var readWeb = (WebView2)typeof(PdfView).GetField("web", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewer)!;
        await WaitUntilAsync(() => readWeb.CoreWebView2 is not null && readWeb.Source?.IsFile == true);
        await PageNavigationTests.ExpectColorAsync(readWeb, Color.Lime);
        Check(window.Text == $"{Path.GetFileName(savedAs)}（{savedAs}）" && address.Text == savedAs);
        window.Close(); Check(window.IsDisposed);
    });

    internal static Task VerifyAddressShortcutAsync(string root) => OnSurfaceAsync(async host =>
    {
        string pdf = Path.Combine(root, "address-shortcut.pdf"); SamplePdf.Write(pdf, "Address shortcut");
        using var window = new PdfWindow(Path.Combine(root, "address-profile-" + Guid.NewGuid().ToString("N"))) { TopLevel = false, Dock = DockStyle.Fill };
        host.Controls.Add(window); window.SetDocument(DocumentPath.Open(pdf)); window.Show();
        var address = window.Controls.Find("AddressBar", true).OfType<TextBox>().Single();
        var viewer = (PdfView)typeof(PdfWindow).GetField("viewer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var web = (WebView2)typeof(PdfView).GetField("web", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewer)!;
        await WaitUntilAsync(() => web.CoreWebView2 is not null && web.Source?.IsFile == true);
        void ClearAddressSelection() { address.Select(0, 0); }
        async Task Selected() => await WaitUntilAsync(() => address.Focused && address.SelectionStart == 0 && address.SelectionLength == pdf.Length);
        void FormShortcut()
        {
            var method = typeof(PdfWindow).GetMethod("ProcessCmdKey", BindingFlags.Instance | BindingFlags.NonPublic)!;
            object[] args = [new Message(), Keys.Alt | Keys.D]; Check((bool)method.Invoke(window, args)!);
        }
        // The WebView2 callback is a separate path from WinForms keyboard routing.
        ClearAddressSelection();
        var keyEvent = new KeyEventArgs(Keys.Alt | Keys.D);
        typeof(Control).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(web, [keyEvent]);
        Check(keyEvent.Handled); await Selected();
        ClearAddressSelection(); FormShortcut(); await Selected(); // Already focused.
        window.ToggleEditMode();
        var editor = (PdfEditor)typeof(PdfWindow).GetField("editor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        await WaitUntilAsync(() => editor.Visible && editor.Viewer.IsPageRendered(0));
        editor.Viewer.Focus(); ClearAddressSelection(); FormShortcut(); await Selected();
        Check(!editor.Session.IsDirty);
        window.ToggleEditMode(); window.ToggleCommentMode();
        var comments = (PdfCommentEditor)typeof(PdfWindow).GetField("commentEditor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        await WaitUntilAsync(() => comments.Visible && comments.Viewer.Selection is not null);
        comments.Viewer.Focus(); ClearAddressSelection(); FormShortcut(); await Selected();
        Check(!comments.Session.IsDirty);
        window.ToggleCommentMode(); host.Controls.Remove(window);
    });

    internal static Task OnSurfaceAsync(Func<Form, Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var host = new PageNavigationTests.RenderTestWindow { ClientSize = new Size(1120, 720) };
                host.Shown += async (_, _) =>
                {
                    try { await action(host); done.TrySetResult(); }
                    catch (Exception ex) { done.TrySetException(ex); }
                    finally { host.Close(); }
                };
                Application.Run(host);
            }
            catch (Exception ex) { done.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return done.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }
    internal static void RevealPage(PdfPageCanvas canvas, int index)
    {
        var rectangle = canvas.PageBounds(index);
        canvas.AutoScrollPosition = new Point(0, Math.Max(0, rectangle.Top - canvas.AutoScrollPosition.Y - 32));
        canvas.Invalidate();
    }
    internal static Point VisibleCenter(PdfPageCanvas canvas, int index)
    {
        var rectangle = Rectangle.Intersect(canvas.PageBounds(index), canvas.ClientRectangle);
        if (rectangle.Width <= 0 || rectangle.Height <= 0) throw new Exception($"Page {index}: {canvas.PageBounds(index)}, client {canvas.ClientRectangle}, scroll {canvas.AutoScrollPosition}, min {canvas.AutoScrollMinSize}, vertical {canvas.VerticalScroll.Maximum} visible {canvas.Visible}");
        return new Point(rectangle.Left + rectangle.Width / 2, rectangle.Top + rectangle.Height / 2);
    }
    internal static async Task ExpectCanvasColorAsync(PdfPageCanvas canvas, int index, Color color)
    {
        await WaitUntilAsync(() => canvas.Visible && canvas.IsPageRendered(index));
        using var bitmap = new Bitmap(canvas.Width, canvas.Height);
        canvas.DrawToBitmap(bitmap, canvas.ClientRectangle);
        var point = VisibleCenter(canvas, index);
        var actual = bitmap.GetPixel(point.X, point.Y);
        if (Math.Abs(actual.R - color.R) > 12 || Math.Abs(actual.G - color.G) > 12 || Math.Abs(actual.B - color.B) > 12)
            throw new Exception($"Canvas page {index + 1}: expected {color}, painted {actual}.");
    }
    internal static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new Exception("Editor command timed out.");
            await Task.Delay(100);
        }
    }

    private static void AssertContent(string path, params int[] expected)
    {
        using var pdf = PdfReader.Open(path, PdfDocumentOpenMode.Modify);
        Check(pdf.PageCount == expected.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            string content = Encoding.ASCII.GetString(pdf.Pages[i].Contents.CreateSingleContent().Stream.UnfilteredValue);
            Check(content.Contains($"Edit fixture - page {expected[i]}", StringComparison.Ordinal));
        }
    }
    private static void Order(PdfEditSession session, params int[] expected) => Check(session.Pages.SequenceEqual(expected));
    private static void Check(bool condition) { if (!condition) throw new Exception("PDF editing assertion failed."); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
}
