namespace SaruPdf.Documents;

// Immutable, self-contained PDF data. Never refers to a live source window/file.
internal sealed class PdfPageBundle(byte[] data, int pageCount)
{
    internal Guid Id { get; } = Guid.NewGuid();
    internal int PageCount { get; } = pageCount;
    internal Stream OpenRead() => new MemoryStream(data, false);
    internal void WriteTo(string path) => File.WriteAllBytes(path, data);
}

// One clipboard per application context; all its PDF windows share it.
internal sealed class PdfPageClipboard
{
    internal PdfPageBundle? Current { get; private set; }
    internal event Action? Changed;
    internal void Set(PdfPageBundle pages) { Current = pages; Changed?.Invoke(); }
    internal void Clear() { Current = null; Changed?.Invoke(); }
}

internal sealed record PdfRenderPage(string Path, uint Page);
