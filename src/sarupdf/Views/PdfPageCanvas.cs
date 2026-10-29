using System.Drawing.Drawing2D;
using SaruPdf.Documents;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;
using NativePdf = Windows.Data.Pdf.PdfDocument;

namespace SaruPdf.Views;

// Own the document surface so pages and gaps can be hit-tested reliably.
internal sealed class PdfPageCanvas : ScrollableControl
{
    private readonly SortedSet<int> selected = [];
    private readonly Dictionary<int, Bitmap> images = [];
    private readonly PageNumberInput pageInput = new();
    private readonly List<Rectangle> bounds = [];
    private NativePdf? pdf;
    private readonly List<SizeF> sizes = [];
    private readonly List<(NativePdf Document, uint Page)> renderPages = [];
    private readonly Dictionary<string, NativePdf> documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim sourceGate = new(1);
    private int[] order = [];
    private int anchor, insertion, epoch, layoutWidth;
    private int hoverGap = -1;
    private bool rendering, renderRequested;
    private float zoom = 1;
    private ContextMenuStrip? popup;
    private int PageGap => Math.Max(32, Font.Height + 12);

    internal IReadOnlyList<int> SelectedPages => selected.ToArray();
    internal int InsertionIndex => insertion;
    [System.ComponentModel.DefaultValue(false)]
    internal bool HasPendingCut { get; set; }
    [System.ComponentModel.DefaultValue(false)]
    internal bool CanPastePages { get; set; }
    [System.ComponentModel.DefaultValue(0)]
    internal int PasteCount { get; set; }
    internal event Action? SelectionChanged;
    internal event Action? CutRequested;
    internal event Action? CopyRequested;
    internal event Action<int>? PasteRequested;
    internal event Action? UndoRequested;
    internal event Action? RedoRequested;
    internal event Action<Exception>? Failed;

    internal PdfPageCanvas()
    {
        Dock = DockStyle.Fill; AutoScroll = true; TabStop = true;
        BackColor = Color.FromArgb(224, 226, 230);
        AccessibleName = "PDFページ編集画面";
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Selectable, true);
    }
    internal async Task LoadAsync(string path)
    {
        var loaded = await NativePdf.LoadFromFileAsync(await StorageFile.GetFileFromPathAsync(path));
        if (IsDisposed) return;
        documents[path] = loaded;
        await LoadSourcesAsync(Enumerable.Range(0, (int)loaded.PageCount).Select(i => new PdfRenderPage(path, (uint)i)).ToArray());
    }
    internal async Task LoadSourcesAsync(IReadOnlyList<PdfRenderPage> sources)
    {
        await sourceGate.WaitAsync();
        try
        {
            int focus = selected.Count == 0 ? -1 : selected.Min;
            var scroll = AutoScrollPosition;
            bool changed = false;
            for (int i = renderPages.Count; i < sources.Count; i++)
            {
                if (IsDisposed) return;
                var source = sources[i];
                if (!documents.TryGetValue(source.Path, out var document))
                {
                    document = await NativePdf.LoadFromFileAsync(await StorageFile.GetFileFromPathAsync(source.Path));
                    if (IsDisposed) return;
                    documents.Add(source.Path, document);
                }
                using var page = document.GetPage(source.Page);
                sizes.Add(new SizeF((float)page.Size.Width, (float)page.Size.Height));
                renderPages.Add((document, source.Page));
                pdf ??= document;
                changed = true;
            }
            if (changed)
            {
                bool followSelection = focus >= 0 && selected.Count > 0 && selected.Min == focus && AutoScrollPosition == scroll;
                epoch++; ClearImages(); LayoutPages();
                if (followSelection) ScrollToPage(focus);
                RequestRender();
            }
        }
        finally { sourceGate.Release(); }
    }
    internal void SetOrder(IEnumerable<int> pages, int start, int count = 1)
    {
        order = pages.ToArray(); selected.Clear();
        for (int i = Math.Max(0, start); i < Math.Min(order.Length, start + count); i++) selected.Add(i);
        anchor = Math.Clamp(start, 0, Math.Max(0, order.Length - 1));
        insertion = Math.Clamp(start, 0, order.Length); hoverGap = -1;
        LayoutPages();
        if (bounds.Count > 0) ScrollToPage(Math.Min(start, bounds.Count - 1));
        SelectionChanged?.Invoke(); Invalidate(); RequestRender();
    }
    internal void SelectPage(int page)
    {
        if (page < 1 || page > order.Length) return;
        SelectIndex(page - 1, Keys.None); ScrollToPage(page - 1);
    }
    internal void SelectEnd()
    {
        selected.Clear(); insertion = order.Length; hoverGap = insertion;
        if (bounds.Count > 0) ScrollToPage(bounds.Count - 1);
        SelectionChanged?.Invoke(); Invalidate();
    }
    private void SelectIndex(int index, Keys modifiers)
    {
        if ((modifiers & Keys.Shift) != 0)
        {
            if ((modifiers & Keys.Control) == 0) selected.Clear();
            for (int i = Math.Min(anchor, index); i <= Math.Max(anchor, index); i++) selected.Add(i);
        }
        else if ((modifiers & Keys.Control) != 0)
        {
            if (!selected.Add(index)) selected.Remove(index);
            anchor = index;
        }
        else { selected.Clear(); selected.Add(index); anchor = index; }
        insertion = index; hoverGap = -1;
        SelectionChanged?.Invoke(); Invalidate();
    }
    internal void SelectAll()
    {
        selected.Clear(); selected.UnionWith(Enumerable.Range(0, order.Length));
        anchor = 0; insertion = 0; SelectionChanged?.Invoke(); Invalidate();
    }
    internal Rectangle PageBounds(int index)
    {
        var rectangle = bounds[index]; rectangle.Offset(AutoScrollPosition); return rectangle;
    }
    internal Point GapPoint(int index)
    {
        int y = index == 0 ? PageGap / 2 : bounds[index - 1].Bottom + PageGap / 2;
        return new Point(ClientSize.Width / 2, y + AutoScrollPosition.Y);
    }
    private (int Page, int Gap) Hit(Point point)
    {
        point.Offset(-AutoScrollPosition.X, -AutoScrollPosition.Y);
        for (int i = 0; i < bounds.Count; i++)
        {
            if (point.Y < bounds[i].Top) return (-1, i);
            if (bounds[i].Contains(point)) return (i, -1);
            if (point.Y <= bounds[i].Bottom) return (-1, -1);
        }
        return (-1, order.Length);
    }
    internal void SelectAt(Point point, Keys modifiers)
    {
        var hit = Hit(point);
        if (hit.Page >= 0) SelectIndex(hit.Page, modifiers);
        else if (hit.Gap >= 0)
        {
            selected.Clear(); insertion = hit.Gap; hoverGap = hit.Gap;
            SelectionChanged?.Invoke(); Invalidate();
        }
    }
    internal ContextMenuStrip CreatePageMenu(Point point)
    {
        var hit = Hit(point);
        var menu = new ContextMenuStrip();
        if (hit.Page >= 0)
        {
            if (!selected.Contains(hit.Page)) SelectIndex(hit.Page, Keys.None);
            menu.Items.Add(new ToolStripMenuItem($"選択した {selected.Count} ページを切り取り", null, (_, _) => CutRequested?.Invoke())
            { Name = "CutPages", Enabled = !HasPendingCut && selected.Count > 0, ShortcutKeyDisplayString = "Ctrl+X" });
            menu.Items.Add(new ToolStripMenuItem($"選択した {selected.Count} ページをコピー", null, (_, _) => CopyRequested?.Invoke())
            { Name = "CopyPages", Enabled = !HasPendingCut && selected.Count > 0, ShortcutKeyDisplayString = "Ctrl+C" });
            AddPaste(menu, "このページの前に貼り付け", hit.Page);
            AddPaste(menu, "このページの後に貼り付け", hit.Page + 1);
        }
        else
        {
            insertion = hit.Gap >= 0 ? hit.Gap : insertion;
            hoverGap = insertion; SelectionChanged?.Invoke(); Invalidate();
            AddPaste(menu, hit.Gap >= 0 ? "ここに貼り付け" : "選択した挿入位置に貼り付け", insertion);
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("すべてのページを選択", null, (_, _) => SelectAll());
        menu.Items.Add("元に戻す", null, (_, _) => UndoRequested?.Invoke());
        menu.Items.Add("やり直し", null, (_, _) => RedoRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("拡大", null, (_, _) => SetZoom(zoom * 1.2f));
        menu.Items.Add("縮小", null, (_, _) => SetZoom(zoom / 1.2f));
        menu.Items.Add("幅に合わせる", null, (_, _) => SetZoom(1));
        return menu;
    }
    private void AddPaste(ContextMenuStrip menu, string text, int index)
    {
        menu.Items.Add(new ToolStripMenuItem(text + (CanPastePages ? $"（{PasteCount}ページ）" : ""), null, (_, _) =>
        { insertion = index; PasteRequested?.Invoke(index); }) { Name = "PasteAt" + index, Enabled = CanPastePages });
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e); Focus(); CancelPageInput();
        if (e.Button == MouseButtons.Left) SelectAt(e.Location, ModifierKeys);
        else if (e.Button == MouseButtons.Right)
        {
            popup?.Dispose(); popup = CreatePageMenu(e.Location); popup.Show(this, e.Location);
        }
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int gap = Hit(e.Location).Gap;
        if (gap != hoverGap) { hoverGap = gap; Invalidate(); }
    }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hoverGap = -1; Invalidate(); }
    internal bool TryHandlePageKey(Keys keyData)
    {
        if ((keyData & Keys.Modifiers) != Keys.None) return false;
        var key = keyData & Keys.KeyCode;
        int digit = key is >= Keys.D0 and <= Keys.D9 ? key - Keys.D0
            : key is >= Keys.NumPad0 and <= Keys.NumPad9 ? key - Keys.NumPad0 : -1;
        if (digit >= 0) { pageInput.Append(digit); Invalidate(); return true; }
        if (!pageInput.IsActive) return false;
        if (key == Keys.Back) { pageInput.Backspace(); Invalidate(); return true; }
        if (key == Keys.Escape) { CancelPageInput(); return true; }
        if (key == Keys.Enter)
        {
            if (pageInput.TryGetPage(out int page) && page <= order.Length) { SelectPage(page); CancelPageInput(); }
            return true;
        }
        CancelPageInput(); return false;
    }
    internal void CancelPageInput() { pageInput.Clear(); Invalidate(); }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.A)) { SelectAll(); return true; }
        if (TryHandlePageKey(keyData)) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if ((ModifierKeys & Keys.Control) != 0)
        {
            SetZoom(e.Delta > 0 ? zoom * 1.2f : zoom / 1.2f);
            if (e is HandledMouseEventArgs handled) handled.Handled = true;
        }
        else base.OnMouseWheel(e);
        RequestRender(); Invalidate();
    }
    protected override void OnScroll(ScrollEventArgs se) { base.OnScroll(se); RequestRender(); Invalidate(); }
    protected override void OnResize(EventArgs e) { base.OnResize(e); LayoutPages(); RequestRender(); }
    private void SetZoom(float value) { zoom = Math.Clamp(value, .25f, 3f); LayoutPages(); RequestRender(); }
    private void ScrollToPage(int index)
    {
        if (index < 0 || index >= bounds.Count) return;
        AutoScrollPosition = new Point(Math.Max(0, -AutoScrollPosition.X), Math.Max(0, bounds[index].Top - PageGap));
        RequestRender(); Invalidate();
    }
    private void LayoutPages()
    {
        if (sizes.Count == 0 || ClientSize.Width <= 0) return;
        int width = Math.Max(120, (int)((ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 48) * zoom));
        if (layoutWidth != width) { layoutWidth = width; epoch++; ClearImages(); }
        float scale = width / Math.Max(sizes.Max(size => size.Width), order.Any(id => id >= sizes.Count) ? 595 : 0);
        int y = PageGap; bounds.Clear();
        foreach (int original in order)
        {
            var size = original < sizes.Count ? sizes[original] : new SizeF(595, 842);
            int w = Math.Max(1, (int)(size.Width * scale));
            int h = Math.Max(1, (int)(size.Height * scale));
            bounds.Add(new Rectangle(24 + (width - w) / 2, y, w, h)); y += h + PageGap;
        }
        AutoScrollMinSize = new Size(width + 48, Math.Max(y + 8, 80)); Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        for (int i = 0; i < bounds.Count; i++)
        {
            var rect = PageBounds(i);
            if (!rect.IntersectsWith(ClientRectangle)) continue;
            e.Graphics.FillRectangle(Brushes.White, rect);
            if (images.TryGetValue(order[i], out var image)) e.Graphics.DrawImage(image, rect);
            else TextRenderer.DrawText(e.Graphics, "描画中…", Font, rect, Color.Gray, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            using var pen = new Pen(selected.Contains(i) ? Color.FromArgb(0, 100, 220) : Color.FromArgb(190, 194, 200), selected.Contains(i) ? 4 : 1);
            e.Graphics.DrawRectangle(pen, rect);
            if (hoverGap != i)
                TextRenderer.DrawText(e.Graphics, $"{i + 1} ページ" + (selected.Contains(i) ? " ✓" : ""), Font, new Rectangle(rect.Left, rect.Top - Font.Height - 4, rect.Width, Font.Height + 4),
                    selected.Contains(i) ? Color.FromArgb(0, 80, 180) : Color.DimGray, TextFormatFlags.Left);
        }
        if (bounds.Count == 0)
            TextRenderer.DrawText(e.Graphics, pdf is null ? "PDFを準備しています…" : "すべてのページを切り取り中です。右クリックで貼り付けてください。", Font, ClientRectangle, Color.DimGray, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        if (hoverGap >= 0 && hoverGap <= bounds.Count && pdf is not null)
        {
            var point = GapPoint(hoverGap);
            using var pen = new Pen(Color.FromArgb(0, 100, 220), 3);
            e.Graphics.DrawLine(pen, 24, point.Y, Math.Max(25, ClientSize.Width - 24), point.Y);
        }
        if (pageInput.IsActive)
        {
            var rect = new Rectangle(Math.Max(0, ClientSize.Width - 340), 8, 320, 34);
            e.Graphics.FillRectangle(Brushes.WhiteSmoke, rect);
            TextRenderer.DrawText(e.Graphics, $"移動先: {pageInput.Text}   Enterで移動 / Escで取消", Font, rect, Color.DarkBlue, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        RequestRender();
    }
    internal bool IsPageRendered(int index) => index >= 0 && index < order.Length && images.ContainsKey(order[index]);
    private void RequestRender()
    {
        if (pdf is null || IsDisposed) return;
        renderRequested = true;
        if (!rendering) _ = RenderVisibleAsync();
    }
    private async Task RenderVisibleAsync()
    {
        rendering = true;
        try
        {
            while (renderRequested && !IsDisposed)
            {
                renderRequested = false;
                int version = epoch;
                var visible = Enumerable.Range(0, bounds.Count).Where(i => PageBounds(i).IntersectsWith(ClientRectangle)).ToArray();
                foreach (int index in visible)
                {
                    if (version != epoch || IsDisposed || index >= order.Length) break;
                    int original = order[index];
                    if (original >= renderPages.Count) continue;
                    if (images.ContainsKey(original)) continue;
                    var size = bounds[index].Size;
                    double reduction = Math.Min(1, Math.Min(3000d / size.Width, Math.Sqrt(8_000_000d / ((double)size.Width * size.Height))));
                    var source = renderPages[original];
                    using var page = source.Document.GetPage(source.Page);
                    using var stream = new InMemoryRandomAccessStream();
                    await page.RenderToStreamAsync(stream, new PdfPageRenderOptions
                    { DestinationWidth = (uint)Math.Max(1, (int)(size.Width * reduction)), DestinationHeight = (uint)Math.Max(1, (int)(size.Height * reduction)) });
                    if (version != epoch || IsDisposed) break;
                    stream.Seek(0);
                    using var adapter = stream.AsStreamForRead();
                    using var decoded = new Bitmap(adapter);
                    var bitmap = new Bitmap(decoded);
                    if (images.Remove(original, out var previous)) previous.Dispose();
                    images.Add(original, bitmap); Invalidate();
                }
                var keep = Enumerable.Range(0, bounds.Count).Where(i => PageBounds(i).IntersectsWith(ClientRectangle)).Select(i => order[i]).ToHashSet();
                foreach (int key in images.Keys.Where(key => !keep.Contains(key)).ToArray())
                    if (images.Count > Math.Max(4, keep.Count)) { images[key].Dispose(); images.Remove(key); }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { if (!IsDisposed) Failed?.Invoke(ex); }
        finally { rendering = false; }
    }
    private void ClearImages() { foreach (var image in images.Values) image.Dispose(); images.Clear(); }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { epoch++; popup?.Dispose(); ClearImages(); pdf = null; documents.Clear(); renderPages.Clear(); sizes.Clear(); }
        base.Dispose(disposing);
    }
}
