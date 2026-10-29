using System.Security.Cryptography;
using PdfSharp.Pdf.IO;

namespace SaruPdf.Documents;

// Page identities refer to the immutable input snapshot, never to a preview file.
internal sealed class PdfEditSession : IDisposable
{
    private sealed record State(int[] Pages, int[] Cut);
    private readonly byte[] original;
    private readonly List<int> pages;
    private readonly Stack<State> undo = new();
    private readonly Stack<State> redo = new();
    private readonly string previewDirectory = Path.Combine(Path.GetTempPath(), "SaruPDF-edit-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> previews = [];
    private int[] savedPages;
    private byte[] savedHash;
    private int[] cut = [];

    internal string SourcePath { get; private set; }
    internal int PageCount => pages.Count;
    internal int TotalPages { get; }
    internal IReadOnlyList<int> Pages => pages.AsReadOnly();
    internal int? CutPage => cut.Length == 0 ? null : cut[0];
    internal IReadOnlyList<int> CutPages => Array.AsReadOnly(cut);
    internal bool HasPendingCut => cut.Length != 0;
    internal bool IsDirty => HasPendingCut || !pages.SequenceEqual(savedPages);
    internal bool CanUndo => undo.Count > 0;
    internal bool CanRedo => redo.Count > 0;

    internal PdfEditSession(string path)
    {
        SourcePath = DocumentPath.Open(path).Path;
        original = File.ReadAllBytes(SourcePath);
        savedHash = SHA256.HashData(original);
        using var input = new MemoryStream(original, false);
        using var pdf = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        if (pdf.SecuritySettings.IsEncrypted)
            throw new InvalidDataException("暗号化されたPDFのページ編集には対応していません。");
        if (pdf.Internals.GetAllObjects().OfType<PdfSharp.Pdf.PdfDictionary>().Any(item =>
            item.Elements.GetName("/Type") == "/Sig" || item.Elements.GetName("/FT") == "/Sig"))
            throw new InvalidDataException("署名のあるPDFのページ編集には対応していません。");
        TotalPages = pdf.PageCount;
        if (TotalPages == 0) throw new InvalidDataException("編集できるページがありません。");
        pages = [.. Enumerable.Range(0, TotalPages)];
        savedPages = [.. pages];
    }

    internal void Cut(int index) => Cut([index]);

    internal void Cut(IEnumerable<int> indexes)
    {
        if (HasPendingCut) throw new InvalidOperationException("切り取り中のページを貼り付けるか、元に戻してください。");
        int[] selected = indexes.Distinct().Order().ToArray();
        if (selected.Length == 0 || selected.Any(index => index < 0 || index >= PageCount))
            throw new ArgumentOutOfRangeException(nameof(indexes));
        Remember();
        cut = selected.Select(index => pages[index]).ToArray();
        foreach (int index in selected.Reverse()) pages.RemoveAt(index);
    }

    // index is a gap in the remaining pages: 0 is the start, Count is the end.
    internal void Paste(int index)
    {
        if (!HasPendingCut) throw new InvalidOperationException("先にページを切り取ってください。");
        if (index < 0 || index > PageCount) throw new ArgumentOutOfRangeException(nameof(index));
        Remember();
        pages.InsertRange(index, cut);
        cut = [];
    }

    internal bool Undo() => Restore(undo, redo);
    internal bool Redo() => Restore(redo, undo);
    private State Snapshot() => new([.. pages], [.. cut]);
    private void Remember() { undo.Push(Snapshot()); redo.Clear(); }
    private bool Restore(Stack<State> from, Stack<State> to)
    {
        if (!from.TryPop(out var state)) return false;
        to.Push(Snapshot());
        pages.Clear(); pages.AddRange(state.Pages); cut = state.Cut;
        return true;
    }

    internal string? WritePreview()
    {
        if (PageCount == 0) return null;
        Directory.CreateDirectory(previewDirectory);
        string path = Path.Combine(previewDirectory, Guid.NewGuid().ToString("N") + ".pdf");
        previews.Add(path);
        File.WriteAllBytes(path, Serialize());
        return path;
    }

    internal string WriteOriginalPreview()
    {
        Directory.CreateDirectory(previewDirectory);
        string path = Path.Combine(previewDirectory, "original.pdf");
        if (!File.Exists(path)) { File.WriteAllBytes(path, original); previews.Add(path); }
        return path;
    }

    private byte[] Serialize()
    {
        using var input = new MemoryStream(original, false);
        using var pdf = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        // Move the original page references in place, preserving their catalog
        // links. Remove/Add re-registers existing objects in PDFsharp 6.2.4.
        List<int> current = [.. Enumerable.Range(0, TotalPages)];
        for (int i = 0; i < pages.Count; i++)
        {
            int oldIndex = current.IndexOf(pages[i]);
            if (oldIndex == i) continue;
            pdf.Pages.MovePage(oldIndex, i);
            current.RemoveAt(oldIndex); current.Insert(i, pages[i]);
        }
        // Only previews can omit a page while it is held by Cut.
        while (pdf.PageCount > pages.Count) pdf.Pages.RemoveAt(pdf.PageCount - 1);
        using var output = new MemoryStream();
        pdf.Save(output, false);
        return output.ToArray();
    }

    internal void Save(string path)
    {
        if (HasPendingCut) throw new InvalidOperationException("切り取り中のページを貼り付けてから保存してください。");
        var target = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(target), ".pdf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("保存先には .pdf ファイルを指定してください。");
        bool samePath = string.Equals(SourcePath, target, StringComparison.OrdinalIgnoreCase);
        if (samePath && (!File.Exists(target) || !SHA256.HashData(File.ReadAllBytes(target)).SequenceEqual(savedHash)))
            throw new IOException("編集中に元のPDFが外部で変更または削除されました。名前を付けて保存してください。");
        byte[] result = Serialize();
        string temporary = Path.Combine(Path.GetDirectoryName(target)!, ".SaruPDF-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(result); stream.Flush(true); }
            // Finish and validate the complete PDF before replacing any destination.
            using (var check = PdfReader.Open(temporary, PdfDocumentOpenMode.Import))
                if (check.PageCount != TotalPages) throw new InvalidDataException("保存するPDFのページ数を検証できませんでした。");
            // Check again after serialization, which may take time for large PDFs.
            if (samePath && (!File.Exists(target) || !SHA256.HashData(File.ReadAllBytes(target)).SequenceEqual(savedHash)))
                throw new IOException("編集中に元のPDFが変更されました。名前を付けて保存してください。");
            if (File.Exists(target)) File.Replace(temporary, target, null);
            else File.Move(temporary, target);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        SourcePath = target;
        savedHash = SHA256.HashData(result);
        savedPages = [.. pages];
    }

    public void Dispose()
    {
        foreach (string path in previews)
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        try { if (Directory.Exists(previewDirectory)) Directory.Delete(previewDirectory); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
