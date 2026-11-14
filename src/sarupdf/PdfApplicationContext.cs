using SaruPdf.Documents;
using SaruPdf.Instances;
using SaruPdf.Views;
using SaruPdf.Updates;

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
    private PdfUpdateDialog? updateDialog;
    private bool preparingUpdate;
    private bool capturingUpdate;

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
                if (stopping || capturingUpdate) { result.TrySetResult(false); return; }
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
        List<Task> restorations = [];
        foreach (var path in request.Paths)
        {
            try
            {
                bool alreadyOpen = catalog.TryFind(path, out _);
                var state = request.Session?.Windows.FirstOrDefault(w => string.Equals(w.Path, path, StringComparison.OrdinalIgnoreCase));
                var window = OpenDocument(path, alreadyOpen ? null : state?.Placement);
                if (!alreadyOpen && state is not null) restorations.Add(RestoreWindowAsync(window, state));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { errors.Add($"{path}\n{DocumentPath.Explain(ex)}"); }
        }

        if (catalog.Windows.Count == 0)
        {
            var empty = CreateWindow(); empty.RestorePlacement(request.Session?.Windows.FirstOrDefault(w => w.Path is null)?.Placement); empty.Show();
        }
        if (request.Paths.Length == 0 || errors.Count > 0)
        {
            var window = catalog.LastActive ?? catalog.Windows[0];
            NativeWindows.Activate(window);
            if (errors.Count > 0) window.ShowError(string.Join("\n\n", errors));
        }
        completed.Add(request.Id);
        recent.Enqueue(request.Id);
        if (recent.Count > 128) completed.Remove(recent.Dequeue());
        if (request.Session is { } session) _ = FinishRestoreAsync(session, restorations);
    }

    private static async Task RestoreWindowAsync(PdfWindow window, PdfWindowState state)
    {
        try { await window.RestoreAfterUpdateAsync(state); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { if (!window.IsDisposed) window.ShowError("更新前の表示状態を復元できませんでした。\n" + ex.Message); }
    }
    private async Task FinishRestoreAsync(UpdateSession session, List<Task> tasks)
    {
        await Task.WhenAll(tasks);
        if (stopping || session.ActiveIndex < 0 || session.ActiveIndex >= session.Windows.Length) return;
        string? path = session.Windows[session.ActiveIndex].Path;
        PdfWindow? window = null;
        if (path is not null) catalog.TryFind(path, out window); else window = catalog.EmptyWindow;
        if (window is not null && !window.IsDisposed) NativeWindows.Activate(window);
    }

    private PdfWindow OpenDocument(string path, WindowPlacement? placement = null)
    {
        // Check the path before reading: reopening preserves the current PDF view even
        // when an external application has since replaced/deleted the original file.
        var normalized = DocumentPath.Normalize(path, Environment.CurrentDirectory);
        if (catalog.TryFind(normalized, out var existing))
        {
            NativeWindows.Activate(existing!);
            return existing!;
        }
        var document = DocumentPath.Open(normalized);
        var window = catalog.EmptyWindow ?? CreateWindow();
        catalog.Assign(document.Path, window);
        window.RestorePlacement(placement);
        window.SetDocument(document);
        NativeWindows.Activate(window);
        return window;
    }

    private PdfWindow CreateWindow()
    {
        var window = new PdfWindow(pageClipboard: pageClipboard);
        catalog.Add(window);
        window.OpenRequested += paths => HandleRequest(OpenRequest.FromArguments(paths, Environment.CurrentDirectory));
        window.UpdateRequested += () => ShowUpdateDialog(window);
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

    private async void ShowUpdateDialog(PdfWindow owner)
    {
        if (updateDialog is not null) { updateDialog.Activate(); return; }
        if (preparingUpdate) return;
        string? package;
        Version? version;
        try
        {
            using var dialog = new PdfUpdateDialog(); updateDialog = dialog;
            if (dialog.ShowDialog(owner) != DialogResult.OK) return;
            package = dialog.PackagePath;
            version = dialog.PackageVersion;
        }
        finally { updateDialog = null; }
        if (package is null) return;
        List<PdfWindow> frozen = [];
        try
        {
            preparingUpdate = true;
            owner.UseWaitCursor = true;
            var update = await Task.Run(() => UpdatePackage.Prepare(package));
            if (stopping || owner.IsDisposed) return;
            if (update.Manifest.Version != version?.ToString()) throw new InvalidDataException("ZIP内のバージョン情報が一致しません。");
            var windows = catalog.Windows.ToArray();
            // Confirm every document before closing any window. Cancelling a
            // later prompt leaves all documents open and never starts setup.
            foreach (var window in windows) if (!window.PrepareForUpdate()) return;
            int active = Math.Max(0, Array.IndexOf(windows, catalog.LastActive));
            capturingUpdate = true;
            foreach (var window in windows) { window.FreezeForUpdate(true); frozen.Add(window); }
            var states = await Task.WhenAll(windows.Select(w => w.CaptureForUpdateAsync()));
            UpdateLauncher.Start(update, new UpdateSession(1, states, active));
            stopping = true;
            foreach (var window in windows) window.CloseForUpdate();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { owner.ShowError("更新を開始できませんでした。\n" + ex.Message); }
        finally
        {
            capturingUpdate = false; preparingUpdate = false;
            foreach (var window in frozen) if (!window.IsDisposed) window.FreezeForUpdate(false);
            if (!owner.IsDisposed) owner.UseWaitCursor = false;
        }
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
