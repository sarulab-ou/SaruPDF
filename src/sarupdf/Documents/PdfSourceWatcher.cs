using System.Security.Cryptography;
using PdfSharp.Pdf.IO;

namespace SaruPdf.Documents;

internal sealed record PdfSourceSnapshot(string Path, byte[] Bytes, byte[] Hash);

// Watch the directory as editors often replace a PDF by renaming a temporary
// file. Debounce notifications and validate a stable snapshot off the UI thread.
internal sealed class PdfSourceWatcher : IDisposable
{
    private readonly string path;
    private readonly FileSystemWatcher watcher;
    private readonly System.Threading.Timer timer;
    private byte[]? knownHash;
    private int running, generation, retries;
    private volatile bool disposed;
    internal event Action<PdfSourceSnapshot>? Changed;
    internal event Action<Exception>? Failed;

    internal PdfSourceWatcher(string path)
    {
        this.path = System.IO.Path.GetFullPath(path);
        try { using var source = Open(); knownHash = SHA256.HashData(source); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        timer = new System.Threading.Timer(async _ => await CheckAsync(), null, Timeout.Infinite, Timeout.Infinite);
        watcher = new FileSystemWatcher(System.IO.Path.GetDirectoryName(this.path)!)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            IncludeSubdirectories = false
        };
        watcher.Changed += (_, e) => Notify(e.FullPath);
        watcher.Created += (_, e) => Notify(e.FullPath);
        watcher.Deleted += (_, e) => Notify(e.FullPath);
        watcher.Renamed += (_, e) => { Notify(e.OldFullPath); Notify(e.FullPath); };
        watcher.Error += (_, _) => Schedule(200);
        watcher.EnableRaisingEvents = true;
        Schedule(200); // Catch a write between reading the initial hash and enabling events.
    }
    private FileStream Open() => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, true);
    private void Notify(string changed)
    {
        if (!string.Equals(path, changed, StringComparison.OrdinalIgnoreCase)) return;
        Interlocked.Increment(ref generation); Interlocked.Exchange(ref retries, 0); Schedule(200);
    }
    private void Schedule(int delay)
    {
        if (disposed) return;
        try { timer.Change(delay, Timeout.Infinite); } catch (ObjectDisposedException) { }
    }
    private async Task CheckAsync()
    {
        if (disposed) return;
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0) { Schedule(100); return; }
        int requested = Volatile.Read(ref generation);
        try
        {
            byte[] bytes;
            await using (var source = Open())
            using (var data = new MemoryStream()) { await source.CopyToAsync(data); bytes = data.ToArray(); }
            byte[] hash = SHA256.HashData(bytes);
            if (knownHash is not null && hash.AsSpan().SequenceEqual(knownHash)) return;
            await Task.Delay(100);
            await using (var stable = Open())
                if (!(await SHA256.HashDataAsync(stable)).AsSpan().SequenceEqual(hash)) { Schedule(200); return; }
            if (requested != Volatile.Read(ref generation)) { Schedule(200); return; }
            using (var input = new MemoryStream(bytes, false))
            using (var pdf = PdfReader.Open(input, PdfDocumentOpenMode.Import))
                if (pdf.PageCount == 0) throw new InvalidDataException("PDFにページがありません。");
            if (disposed || requested != Volatile.Read(ref generation)) return;
            knownHash = hash; retries = 0;
            Changed?.Invoke(new(path, bytes, hash));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!disposed)
            {
                if (Interlocked.Increment(ref retries) < 20) Schedule(250);
                else Failed?.Invoke(ex);
            }
        }
        finally
        {
            Interlocked.Exchange(ref running, 0);
            if (!disposed && requested != Volatile.Read(ref generation)) Schedule(200);
        }
    }
    public void Dispose()
    {
        disposed = true; watcher.Dispose(); timer.Dispose();
    }
}
