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

    internal static Task VerifyEditorRenderingAsync(string root)
    {
        string pdf = Path.Combine(root, "editor-colors.pdf");
        PageNavigationTests.WriteColorPdf(pdf);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
            using var host = new PageNavigationTests.RenderTestWindow { ClientSize = new Size(1120, 720) };
            using var editor = new PdfEditor(new PdfEditSession(pdf), Path.Combine(root, "edit-render-profile-" + Guid.NewGuid().ToString("N")));
            host.Controls.Add(editor);
            editor.Failed += ex => done.TrySetException(ex);
            host.Shown += async (_, _) =>
            {
                try
                {
                    var web = (WebView2)typeof(PdfView).GetField("web", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor.Viewer)!;
                    var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    web.NavigationCompleted += (_, e) => { if (e.IsSuccess && web.Source?.IsFile == true) loaded.TrySetResult(); };
                    await editor.LoadAsync();
                    await loaded.Task.WaitAsync(TimeSpan.FromSeconds(12));
                    await PageNavigationTests.ExpectColorAsync(web, Color.Red);
                    editor.Controls.Find("CutPage", true).OfType<Button>().Single().PerformClick();
                    Check(editor.Session.HasPendingCut);
                    await PageNavigationTests.ExpectColorAsync(web, Color.Lime);
                    editor.SelectEnd();
                    editor.Controls.Find("PastePage", true).OfType<Button>().Single().PerformClick();
                    Check(!editor.Session.HasPendingCut && editor.Session.PageCount == 12 && editor.Session.IsDirty);
                    await PageNavigationTests.ExpectColorAsync(web, Color.Red);
                    // Number navigation updates the selected page used by Cut.
                    Check(editor.Viewer.TryHandlePageKey(Keys.D1)); Check(editor.Viewer.TryHandlePageKey(Keys.Enter));
                    await PageNavigationTests.ExpectColorAsync(web, Color.Lime); Check(editor.SelectedIndex == 0);
                    string savedPath = Path.Combine(root, "editor-render-saved.pdf");
                    editor.Session.Save(savedPath); editor.UpdateState();
                    using (var saved = PdfReader.Open(savedPath, PdfDocumentOpenMode.Import)) Check(saved.PageCount == 12);
                    editor.Viewer.NavigateDocument(DocumentPath.Open(savedPath));
                    await PageNavigationTests.ExpectColorAsync(web, Color.Lime);
                    editor.Viewer.NavigateDocument(DocumentPath.Open(savedPath), 12);
                    await PageNavigationTests.ExpectColorAsync(web, Color.Red);
                    editor.Undo(); Check(editor.Session.HasPendingCut);
                    editor.Undo(); Check(!editor.Session.HasPendingCut && editor.Session.IsDirty);
                    await PageNavigationTests.ExpectColorAsync(web, Color.Red);
                    done.TrySetResult();
                }
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

    internal static Task VerifyWindowCommandsAsync(string root)
    {
        string pdf = Path.Combine(root, "window-edit-colors.pdf");
        PageNavigationTests.WriteColorPdf(pdf);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var host = new PageNavigationTests.RenderTestWindow { ClientSize = new Size(1120, 720) };
                using var window = new PdfWindow(Path.Combine(root, "window-edit-profile-" + Guid.NewGuid().ToString("N"))) { TopLevel = false, Dock = DockStyle.Fill };
                host.Controls.Add(window);
                var menu = window.MainMenuStrip!;
                var editMode = menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "編集(&E)").DropDownItems.OfType<ToolStripMenuItem>().Single(item => item.Name == "EditMode");
                Check(!editMode.Enabled);
                window.SetDocument(DocumentPath.Open(pdf)); Check(editMode.Enabled);
                host.Shown += async (_, _) =>
                {
                    try
                    {
                        window.Show();
                        editMode.PerformClick(); Check(editMode.Checked);
                        var editor = (PdfEditor)typeof(PdfWindow).GetField("editor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                        var web = (WebView2)typeof(PdfView).GetField("web", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor.Viewer)!;
                        await WaitUntilAsync(() => web.CoreWebView2 is not null && web.Source?.IsFile == true);
                        await PageNavigationTests.ExpectColorAsync(web, Color.Red);
                        bool Key(Keys keys)
                        {
                            var method = typeof(PdfWindow).GetMethod("ProcessCmdKey", BindingFlags.Instance | BindingFlags.NonPublic)!;
                            object[] args = [new Message(), keys]; return (bool)method.Invoke(window, args)!;
                        }
                        Check(Key(Keys.Control | Keys.X));
                        await WaitUntilAsync(() => editor.Session.HasPendingCut);
                        await PageNavigationTests.ExpectColorAsync(web, Color.Lime);
                        editor.SelectEnd(); Check(Key(Keys.Control | Keys.V));
                        await WaitUntilAsync(() => !editor.Session.HasPendingCut);
                        await PageNavigationTests.ExpectColorAsync(web, Color.Red);
                        Check(Key(Keys.Control | Keys.S));
                        await WaitUntilAsync(() => !editor.Session.IsDirty);
                        using (var check = PdfReader.Open(pdf, PdfDocumentOpenMode.Import)) Check(check.PageCount == 12);
                        editMode.PerformClick(); Check(!editMode.Checked);
                        var viewer = (PdfView)typeof(PdfWindow).GetField("viewer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                        var readWeb = (WebView2)typeof(PdfView).GetField("web", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewer)!;
                        await PageNavigationTests.ExpectColorAsync(readWeb, Color.Lime);
                        Check(window.Text == $"{Path.GetFileName(pdf)}（{pdf}）");
                        window.Close(); Check(window.IsDisposed);
                        done.TrySetResult();
                    }
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

    private static async Task WaitUntilAsync(Func<bool> condition)
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
