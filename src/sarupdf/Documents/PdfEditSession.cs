using System.Security.Cryptography;
using PdfSharp.Pdf.IO;

namespace SaruPdf.Documents;

// Stable page IDs refer to immutable original/copied PDF snapshots.
internal sealed class PdfEditSession : IDisposable
{
    private sealed record State(int[] Pages, int[] Cut, PdfCommentMark[] Comments);
    private readonly byte[] original;
    private readonly List<int> pages;
    private sealed record ImportedPage(PdfPageBundle Bundle, int Page);
    private readonly List<ImportedPage> imported = [];
    private readonly Dictionary<Guid, string> bundlePreviews = [];
    private readonly Stack<State> undo = new();
    private readonly Stack<State> redo = new();
    private readonly string previewDirectory = Path.Combine(Path.GetTempPath(), "SaruPDF-edit-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> previews = [];
    private int[] savedPages;
    private byte[] savedHash;
    private int[] cut = [];
    private readonly List<PdfCommentMark> comments = [];
    private PdfCommentMark[] savedComments = [];
    private readonly HashSet<Guid> originalCommentIds = [];
    internal IReadOnlyList<PdfCommentMark> Comments => comments.AsReadOnly();

    internal string SourcePath { get; private set; }
    internal int PageCount => pages.Count;
    internal int TotalPages { get; }
    internal IReadOnlyList<int> Pages => pages.AsReadOnly();
    internal int? CutPage => cut.Length == 0 ? null : cut[0];
    internal IReadOnlyList<int> CutPages => Array.AsReadOnly(cut);
    internal bool HasPendingCut => cut.Length != 0;
    internal bool IsDirty => HasPendingCut || !pages.SequenceEqual(savedPages) || !comments.SequenceEqual(savedComments);
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
        comments.AddRange(PdfComments.ReadManaged(pdf));
        originalCommentIds.UnionWith(comments.Select(comment => comment.Id));
        savedComments = [.. comments];
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

    internal PdfPageBundle Copy(IEnumerable<int> indexes)
    {
        if (HasPendingCut) throw new InvalidOperationException("切り取り中のページを貼り付けてからコピーしてください。");
        int[] selected = indexes.Distinct().Order().ToArray();
        if (selected.Length == 0 || selected.Any(index => index < 0 || index >= PageCount))
            throw new ArgumentOutOfRangeException(nameof(indexes));
        using var composed = new MemoryStream(Serialize(), false);
        using var source = PdfReader.Open(composed, PdfDocumentOpenMode.Import);
        using var output = new PdfSharp.Pdf.PdfDocument();
        foreach (int index in selected) output.Pages.Add(source.Pages[index]);
        using var stream = new MemoryStream();
        output.Save(stream, false);
        return new PdfPageBundle(stream.ToArray(), selected.Length);
    }

    internal void PasteCopy(int index, PdfPageBundle bundle)
    {
        if (HasPendingCut) throw new InvalidOperationException("切り取り中のページを貼り付けてからコピーを挿入してください。");
        if (index < 0 || index > PageCount) throw new ArgumentOutOfRangeException(nameof(index));
        Remember();
        InsertCopy(index, bundle);
    }

    private void InsertCopy(int index, PdfPageBundle bundle)
    {
        int first = TotalPages + imported.Count;
        for (int i = 0; i < bundle.PageCount; i++) imported.Add(new ImportedPage(bundle, i));
        pages.InsertRange(index, Enumerable.Range(first, bundle.PageCount));
    }

    internal int ImportFiles(int index, IEnumerable<string> paths)
    {
        if (HasPendingCut) throw new InvalidOperationException("切り取り中のページを貼り付けてからインポートしてください。");
        if (index < 0 || index > PageCount) throw new ArgumentOutOfRangeException(nameof(index));
        List<PdfPageBundle> bundles = [];
        foreach (string path in paths)
        {
            using var input = new PdfEditSession(path);
            bundles.Add(input.Copy(Enumerable.Range(0, input.PageCount)));
        }
        if (bundles.Count == 0) throw new ArgumentException("インポートするPDFを選択してください。", nameof(paths));
        // Read and validate all input before changing this document or its history.
        Remember();
        int count = 0;
        foreach (var bundle in bundles) { InsertCopy(index + count, bundle); count += bundle.PageCount; }
        return count;
    }

    internal bool Undo() => Restore(undo, redo);
    internal bool Redo() => Restore(redo, undo);
    internal void AddComment(IEnumerable<PdfCommentRegion> regions, string text)
    {
        if (HasPendingCut) throw new InvalidOperationException("切り取り中のページを貼り付けてください。");
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("コメントを入力してください。", nameof(text));
        var selected = regions.ToArray();
        if (selected.Length == 0 || selected.Any(region => region.Page < 0 || region.Page >= pages.Count || region.Quads.Count == 0 ||
            region.Quads.SelectMany(q => q.Points).Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y))))
            throw new ArgumentException("コメントの選択範囲が正しくありません。", nameof(regions));
        Remember();
        foreach (var region in selected)
            comments.Add(new PdfCommentMark(Guid.NewGuid(), pages[region.Page], text,
                Array.AsReadOnly(region.Quads.ToArray()), DateTime.UtcNow));
    }
    internal void UpdateComment(Guid id, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("コメントを入力してください。", nameof(text));
        int index = comments.FindIndex(comment => comment.Id == id);
        if (index < 0) throw new ArgumentException("コメントが見つかりません。", nameof(id));
        text = text.ReplaceLineEndings("\r");
        if (comments[index].Text.ReplaceLineEndings("\r") == text) return;
        Remember(); comments[index] = comments[index] with { Text = text, Modified = DateTime.UtcNow };
    }
    private State Snapshot() => new([.. pages], [.. cut], [.. comments]);
    private void Remember() { undo.Push(Snapshot()); redo.Clear(); }
    private bool Restore(Stack<State> from, Stack<State> to)
    {
        if (!from.TryPop(out var state)) return false;
        to.Push(Snapshot());
        pages.Clear(); pages.AddRange(state.Pages); cut = state.Cut;
        comments.Clear(); comments.AddRange(state.Comments);
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
    internal string WriteCommentPreview()
    {
        if (originalCommentIds.Count == 0) return WriteOriginalPreview();
        Directory.CreateDirectory(previewDirectory);
        string path = Path.Combine(previewDirectory, "comment-base.pdf");
        if (File.Exists(path)) return path;
        using var input = new MemoryStream(original, false);
        using var pdf = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        PdfComments.RemoveManaged(pdf, originalCommentIds);
        previews.Add(path); pdf.Save(path); return path;
    }

    internal IReadOnlyList<PdfRenderPage> RenderPages()
    {
        List<PdfRenderPage> result = [];
        if (TotalPages > 0)
        {
            string originalPath = WriteOriginalPreview();
            result.AddRange(Enumerable.Range(0, TotalPages).Select(i => new PdfRenderPage(originalPath, (uint)i)));
        }
        if (imported.Count > 0) Directory.CreateDirectory(previewDirectory);
        foreach (var page in imported)
        {
            if (!bundlePreviews.TryGetValue(page.Bundle.Id, out string? path))
            {
                path = Path.Combine(previewDirectory, page.Bundle.Id.ToString("N") + ".pdf");
                page.Bundle.WriteTo(path); previews.Add(path); bundlePreviews.Add(page.Bundle.Id, path);
            }
            result.Add(new PdfRenderPage(path, (uint)page.Page));
        }
        return result;
    }

    private byte[] Serialize()
    {
        using var input = new MemoryStream(original, false);
        using var pdf = TotalPages == 0 ? new PdfSharp.Pdf.PdfDocument() : PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        PdfComments.RemoveManaged(pdf, originalCommentIds);
        // Move the original page references in place, preserving their catalog
        // links. Remove/Add re-registers existing objects in PDFsharp 6.2.4.
        List<int> current = [.. Enumerable.Range(0, TotalPages)];
        int[] originals = pages.Where(page => page < TotalPages).ToArray();
        for (int i = 0; i < originals.Length; i++)
        {
            int oldIndex = current.IndexOf(originals[i]);
            if (oldIndex == i) continue;
            pdf.Pages.MovePage(oldIndex, i);
            current.RemoveAt(oldIndex); current.Insert(i, originals[i]);
        }
        // Only previews can omit a page while it is held by Cut.
        while (pdf.PageCount > originals.Length) pdf.Pages.RemoveAt(pdf.PageCount - 1);
        var sources = new Dictionary<PdfPageBundle, PdfSharp.Pdf.PdfDocument>();
        var streams = new List<Stream>();
        try
        {
            for (int i = 0; i < pages.Count; i++)
            {
                if (pages[i] < TotalPages) continue;
                var page = imported[pages[i] - TotalPages];
                if (!sources.TryGetValue(page.Bundle, out var source))
                {
                    var stream = page.Bundle.OpenRead(); streams.Add(stream);
                    source = PdfReader.Open(stream, PdfDocumentOpenMode.Import); sources.Add(page.Bundle, source);
                }
                pdf.Pages.Insert(i, source.Pages[page.Page]);
            }
            for (int i = 0; i < pages.Count; i++)
                foreach (var comment in comments.Where(comment => comment.PageId == pages[i])) PdfComments.Write(pdf, pdf.Pages[i], comment);
            using var output = new MemoryStream();
            pdf.Save(output, false);
            return output.ToArray();
        }
        finally
        {
            foreach (var source in sources.Values) source.Dispose();
            foreach (var stream in streams) stream.Dispose();
        }
    }

    internal void Save(string path)
    {
        if (HasPendingCut) throw new InvalidOperationException("切り取り中のページを貼り付けてから保存してください。");
        if (PageCount == 0) throw new InvalidOperationException("ページを貼り付けるかPDFをインポートしてから保存してください。");
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
                if (check.PageCount != pages.Count) throw new InvalidDataException("保存するPDFのページ数を検証できませんでした。");
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
        savedComments = [.. comments];
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
