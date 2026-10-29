using SaruPdf.Documents;
using SaruPdf.Instances;
using SaruPdf.Views;

namespace SaruPdf;

internal sealed class PdfApplicationContext : ApplicationContext
{
    private readonly Control dispatcher = new();
    private readonly WindowCatalog<PdfWindow> catalog = new();
    private readonly PdfPageClipboard pageClipboard = new();
    private readonly HashSet<Guid> completed = [];
    private readonly Queue<Guid> recent = new();
    private readonly System.Windows.Forms.Timer exitTimer = new() { Interval = 200 };
    private bool stopping;

    public PdfApplicationContext()
    {
        _ = dispatcher.Handle;
        exitTimer.Tick += (_, _) =>
        {
            exitTimer.Stop();
            if (catalog.Windows.Count != 0) return;
            stopping = true;
            ExitThread();
        };
    }

    public void OpenInitial(OpenRequest request) => HandleRequest(request);

    public Task<bool> DispatchAsync(OpenRequest request)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            dispatcher.BeginInvoke(() =>
            {
                if (stopping) { result.TrySetResult(false); return; }
                try { HandleRequest(request); result.TrySetResult(true); }
                catch (Exception ex) { result.TrySetException(ex); }
            });
        }
        catch (InvalidOperationException) { result.TrySetResult(false); }
        return result.Task;
    }

    private void HandleRequest(OpenRequest request)
    {
        if (completed.Contains(request.Id)) return;
        exitTimer.Stop();
        List<string> errors = [.. request.Errors];
        foreach (var path in request.Paths)
        {
            try { OpenDocument(path); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { errors.Add($"{path}\n{DocumentPath.Explain(ex)}"); }
        }

        if (catalog.Windows.Count == 0) CreateWindow().Show();
        if (request.Paths.Length == 0 || errors.Count > 0)
        {
            var window = catalog.LastActive ?? catalog.Windows[0];
            NativeWindows.Activate(window);
            if (errors.Count > 0) window.ShowError(string.Join("\n\n", errors));
        }
        completed.Add(request.Id);
        recent.Enqueue(request.Id);
        if (recent.Count > 128) completed.Remove(recent.Dequeue());
    }

    private void OpenDocument(string path)
    {
        // Check the path before reading: reopening preserves the current PDF view even
        // when an external application has since replaced/deleted the original file.
        var normalized = DocumentPath.Normalize(path, Environment.CurrentDirectory);
        if (catalog.TryFind(normalized, out var existing))
        {
            NativeWindows.Activate(existing!);
            return;
        }
        var document = DocumentPath.Open(normalized);
        var window = catalog.EmptyWindow ?? CreateWindow();
        catalog.Assign(document.Path, window);
        window.SetDocument(document);
        NativeWindows.Activate(window);
    }

    private PdfWindow CreateWindow()
    {
        var window = new PdfWindow(pageClipboard: pageClipboard);
        catalog.Add(window);
        window.OpenRequested += paths => HandleRequest(OpenRequest.FromArguments(paths, Environment.CurrentDirectory));
        window.NewWindowRequested += () =>
        {
            exitTimer.Stop();
            var created = CreateWindow();
            catalog.MarkOccupied(created);
            created.SetNewDocument();
            NativeWindows.Activate(created);
        };
        window.CanUseDocumentPath = path => !catalog.TryFind(path, out var existing) || ReferenceEquals(existing, window);
        window.DocumentPathChanged += document => catalog.Reassign(document.Path, window);
        window.Activated += (_, _) => catalog.Activate(window);
        window.FormClosed += (_, _) =>
        {
            catalog.Remove(window);
            if (catalog.Windows.Count == 0) exitTimer.Start();
        };
        return window;
    }

    public void ReportPipeFailure(Exception error)
    {
        try
        {
            dispatcher.BeginInvoke(() =>
            {
                if (!stopping && catalog.Windows.Count > 0)
                    (catalog.LastActive ?? catalog.Windows[0]).ShowError($"追加起動の受け付けで問題が発生しました。要求が失敗する場合は SaruPDF を終了して起動し直してください。\n{error.Message}");
            });
        }
        catch (InvalidOperationException) { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            stopping = true;
            exitTimer.Dispose();
            foreach (var window in catalog.Windows.ToArray()) window.Dispose();
            pageClipboard.Clear();
            dispatcher.Dispose();
        }
        base.Dispose(disposing);
    }
}
