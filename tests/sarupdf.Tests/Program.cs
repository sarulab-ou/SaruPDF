using System.Buffers.Binary;
using System.IO.Pipes;
using SaruPdf.Documents;
using SaruPdf.Instances;

namespace SaruPdf.Tests;

internal static class Program
{
    private static int passed;
    private static int failed;
    private static string root = "";

    private static async Task<int> Main()
    {
        root = Path.GetFullPath("artifacts/test-data");
        Directory.CreateDirectory(root);
        string a = Path.Combine(root, "論文 A #100% &.pdf");
        string b = Path.Combine(root, "B.pdf");
        SamplePdf.Write(a, "Document A");
        SamplePdf.Write(b, "Document B");
        SamplePdf.Write(Path.Combine(root, "別フォルダー", Path.GetFileName(a)), "Different folder");

        await Test("relative paths resolve in the launching process directory", () =>
        {
            var request = OpenRequest.FromArguments([".\\別フォルダー\\..\\" + Path.GetFileName(a)], root);
            Equal(a, request.Paths.Single());
            Equal(0, request.Errors.Length);
        });
        await Test("file URI round-trips Japanese, spaces, #, %, &", () =>
        {
            var doc = DocumentPath.Open(a);
            Equal(a, doc.Uri.LocalPath);
            Check(doc.Uri.AbsoluteUri.Contains("%23") && doc.Uri.AbsoluteUri.Contains("%25"));
            Check(DocumentPath.IsSameDocument(new Uri(doc.Uri.AbsoluteUri + "#page=2&zoom=150"), doc));
            Check(!DocumentPath.IsSameDocument(new Uri(b), doc));
            Check(!DocumentPath.IsSameDocument(new Uri("file:///C:/bad%00.pdf"), doc));
        });
        await Test("case-insensitive lookup registers before rendering", () =>
        {
            var catalog = new WindowCatalog<object>();
            var window = new object();
            catalog.Add(window);
            catalog.Assign(a, window);
            Check(catalog.TryFind(a.ToUpperInvariant(), out var found) && ReferenceEquals(window, found));
            Equal(1, catalog.Windows.Count);
            Check(catalog.EmptyWindow is null);
        });
        await Test("same basename in different directories stays distinct", () =>
        {
            var catalog = new WindowCatalog<object>();
            var first = new object(); var second = new object();
            catalog.Add(first); catalog.Assign(a, first);
            catalog.Add(second); catalog.Assign(Path.Combine(root, "別フォルダー", Path.GetFileName(a)), second);
            Equal(2, catalog.Windows.Count);
            Check(catalog.TryFind(a, out var found) && ReferenceEquals(first, found));
        });
        await Test("closing first window retains the second; final close empties catalog", () =>
        {
            var catalog = new WindowCatalog<object>();
            var first = new object(); var second = new object();
            catalog.Add(first); catalog.Assign(a, first);
            catalog.Add(second); catalog.Assign(b, second);
            catalog.Remove(first);
            Equal(1, catalog.Windows.Count);
            Check(ReferenceEquals(second, catalog.LastActive));
            Check(!catalog.TryFind(a, out _));
            catalog.Remove(second);
            Equal(0, catalog.Windows.Count);
            Check(catalog.LastActive is null);
        });
        await Test("replacement at same path does not leave a stale file id", () =>
        {
            string path = Path.Combine(root, "replacement.pdf");
            SamplePdf.Write(path, "Before");
            var original = DocumentPath.Open(path);
            SamplePdf.Write(path, "After");
            Equal(original.Path, DocumentPath.Open(path).Path);
        });
        await Test("missing, wrong extension, false PDF, locked file are rejected", () =>
        {
            Throws<FileNotFoundException>(() => DocumentPath.Open(Path.Combine(root, "missing.pdf")));
            File.WriteAllText(Path.Combine(root, "text.pdf"), "not a pdf");
            Throws<InvalidDataException>(() => DocumentPath.Open(Path.Combine(root, "text.pdf")));
            Throws<InvalidDataException>(() => DocumentPath.Open(Path.Combine(root, "text.txt")));
            using var locked = new FileStream(b, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Throws<IOException>(() => DocumentPath.Open(b));
        });
        await Test("mixed invalid-file requests finish and keep an initial window", async () =>
        {
            var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    using var context = new PdfApplicationContext();
                    context.OpenInitial(OpenRequest.FromArguments([Path.Combine(root, "missing.pdf"), Path.Combine(root, "text.pdf")], root));
                    Equal(1, Application.OpenForms.Count);
                    Equal("SaruPDF", Application.OpenForms[0]!.Text);
                    result.SetResult();
                }
                catch (Exception ex) { result.SetException(ex); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            await result.Task.WaitAsync(TimeSpan.FromSeconds(5));
            thread.Join();
        });
        await Test("pipe protocol round-trips Unicode and multiple paths", async () =>
        {
            using var stream = new MemoryStream();
            var request = OpenRequest.FromArguments([a, b], root);
            await PipeProtocol.WriteAsync(stream, request, CancellationToken.None);
            stream.Position = 0;
            var result = await PipeProtocol.ReadAsync<OpenRequest>(stream, CancellationToken.None);
            Equal(request.Id, result.Id);
            Check(result.Paths.SequenceEqual(request.Paths));
        });
        await Test("oversized and truncated pipe frames are rejected", async () =>
        {
            byte[] header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, PipeProtocol.MaximumBytes + 1);
            using var huge = new MemoryStream(header);
            await ThrowsAsync<InvalidDataException>(() => PipeProtocol.ReadAsync<OpenRequest>(huge, CancellationToken.None));
            using var shortStream = new MemoryStream([1, 0]);
            await ThrowsAsync<EndOfStreamException>(() => PipeProtocol.ReadAsync<OpenRequest>(shortStream, CancellationToken.None));
        });
        await Test("real named pipe waits for dispatch before acknowledging", async () =>
        {
            string name = "SaruPDF.test." + Guid.NewGuid().ToString("N");
            var received = new TaskCompletionSource<OpenRequest>();
            var commit = new TaskCompletionSource<bool>();
            using var server = new InstancePipe(name, req => { received.TrySetResult(req); return commit.Task; });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var request = OpenRequest.FromArguments([a, b], root);
            var forward = InstancePipe.ForwardAsync(name, request, timeout.Token);
            var delivered = await received.Task.WaitAsync(timeout.Token);
            Equal(a, delivered.Paths[0]);
            Check(!forward.IsCompleted);
            commit.SetResult(true);
            Check(await forward);
        });
        await Test("disconnected launcher cannot stop later requests", async () =>
        {
            string name = "SaruPDF.test." + Guid.NewGuid().ToString("N");
            using var server = new InstancePipe(name, _ => Task.FromResult(true));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
            { await client.ConnectAsync(timeout.Token); }
            Check(await InstancePipe.ForwardAsync(name, OpenRequest.FromArguments([a], root), timeout.Token));
        });
        await Test("shutdown rejection reaches the launcher for retry", async () =>
        {
            string name = "SaruPDF.test." + Guid.NewGuid().ToString("N");
            using var server = new InstancePipe(name, _ => Task.FromResult(false));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            Check(!await InstancePipe.ForwardAsync(name, OpenRequest.FromArguments([a], root), timeout.Token));
        });
        await Test("a failed dispatch rejects the request without stopping the pipe", async () =>
        {
            string name = "SaruPDF.test." + Guid.NewGuid().ToString("N");
            int calls = 0; int errors = 0;
            using var server = new InstancePipe(name, _ => Interlocked.Increment(ref calls) == 1
                ? Task.FromException<bool>(new InvalidOperationException("simulated dispatch failure")) : Task.FromResult(true));
            server.Failed += _ => Interlocked.Increment(ref errors);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            Check(!await InstancePipe.ForwardAsync(name, OpenRequest.FromArguments([a], root), timeout.Token));
            Check(await InstancePipe.ForwardAsync(name, OpenRequest.FromArguments([b], root), timeout.Token));
            Equal(1, errors);
        });
        await Test("an absent pipe is bounded by cancellation", async () =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            await ThrowsAsync<OperationCanceledException>(() => InstancePipe.ForwardAsync("SaruPDF.absent." + Guid.NewGuid(), OpenRequest.FromArguments([], root), timeout.Token));
        });

        foreach (var (name, run) in AssociationTests.Cases()) await Test(name, run);
        foreach (var (name, run) in UiRegressionTests.Cases(a)) await Test(name, run);
        foreach (var (name, run) in PageNavigationTests.Cases()) await Test(name, run);
        await Test("rendered PDF changes pages with number + Enter and respects Escape", () => PageNavigationTests.VerifyRenderingAsync(root));
        foreach (var (name, run) in PdfEditingTests.Cases(root)) await Test(name, run);
        await Test("direct page selection and gap context menu move multiple rendered pages", () => PdfEditingTests.VerifyEditorRenderingAsync(root));
        await Test("Edit menu and Ctrl+X/V/S save the reordered PDF and return to viewing", () => PdfEditingTests.VerifyWindowCommandsAsync(root));
        foreach (var (name, run) in PdfCopyTests.Cases(root)) await Test(name, run);
        await Test("two PDF windows copy multiple pages, close source, paste repeatedly and save real rendering", () => PdfCopyTests.VerifyWindowsAsync(root));
        foreach (var (name, run) in PageOperationTests.Cases(root)) await Test(name, run);
        await Test("page-operation plus/minus shortcuts zoom and preserve the visible page", () => PageOperationTests.VerifyZoomAsync(root));
        await Test("boundary import menu inserts all PDF pages into existing documents", () => PageOperationTests.VerifyBoundaryImportAsync(root));
        await Test("page-operation mode inherits actual PDF zoom and removes Ctrl+N", () => PageOperationTests.VerifyInheritedZoomAsync(root));
        await Test("arrow keys move pages and Shift extends, shrinks and reverses the selection", () => PageOperationTests.VerifyArrowSelectionAsync(root));
        await Test("page-operation mode sets and restores native title bar colors", () => PageOperationTests.VerifyTitleBarAsync(root));
        await Test("current page stays centered and has a different visual marker from selected pages", () => PageOperationTests.VerifyCurrentPageAsync(root));
        foreach (var (name, run) in PdfCommentTests.Cases(root)) await Test(name, run);
        await Test("comment mode caret, Enter dialog, undo, save and independent highlight rendering", () => PdfCommentTests.VerifyUiAsync(root));
        await Test("English and Japanese comments remain red in the normal WebView2 PDF viewer", () => PdfCommentTests.VerifyBrowserAsync(root));
        await Test("drag selection, right-click add, saved comment editing and explicit modes", () => PdfCommentTests.VerifyDragEditModesAsync(root));
        Console.WriteLine($"{passed} passed, {failed} failed. Samples: {root}");
        return failed == 0 ? 0 : 1;
    }

    private static Task Test(string name, Action run) => Test(name, () => { run(); return Task.CompletedTask; });
    private static async Task Test(string name, Func<Task> run)
    {
        try { await run(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {name}: {ex}"); }
    }
    private static void Check(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual));
    private static void Throws<T>(Action run) where T : Exception
    { try { run(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static async Task ThrowsAsync<T>(Func<Task> run) where T : Exception
    { try { await run(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
}
