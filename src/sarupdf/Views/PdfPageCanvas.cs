using System.Drawing.Drawing2D;
using SaruPdf.Documents;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;
using NativePdf = Windows.Data.Pdf.PdfDocument;

namespace SaruPdf.Views;

// Own the document surface so pages and gaps can be hit-tested reliably.
internal class PdfPageCanvas : ScrollableControl
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
    private int anchor, currentPage, insertion, epoch, layoutWidth;
    private int hoverGap = -1;
    private bool rendering, renderRequested;
    private bool sourcesReady;
    private bool insertionIsGap;
    private float zoom = 1;
    private PdfPageViewState? initialView;
    private float? pixelsPerUnit;
    private ContextMenuStrip? popup;
    private int PageGap => Math.Max(32, Font.Height + 12);

    internal IReadOnlyList<int> SelectedPages => selected.ToArray();
    internal int CurrentPage => order.Length == 0 ? -1 : currentPage;
    [System.ComponentModel.DefaultValue(true)]
    internal bool ShowPageSelection { get; set; } = true;
    internal int PageCount => order.Length;
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
    internal event Action<int>? ImportRequested;
    internal event Action? UndoRequested;
    internal event Action? RedoRequested;
    internal event Action<Exception>? Failed;

    internal PdfPageCanvas()
    {
        Dock = DockStyle.Fill; AutoScroll = true; TabStop = true;
        BackColor = Color.FromArgb(224, 226, 230);
        AccessibleName = "PDFページ操作画面";
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Selectable, true);
    }
    internal void InheritView(PdfPageViewState? state) => initialView = state;
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
            sourcesReady = true;
            int focus = CurrentPage;
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
                if (initialView is { } state && state.Page < sizes.Count)
                {
                    pixelsPerUnit = (float)(state.Width / sizes[state.Page].Width);
                    focus = state.Page;
                    selected.Clear(); selected.Add(focus);
                    anchor = currentPage = insertion = focus;
                    insertionIsGap = false;
                    initialView = null;
                }
                bool followSelection = focus >= 0 && CurrentPage == focus && AutoScrollPosition == scroll;
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
        currentPage = anchor;
        insertion = Math.Clamp(start, 0, order.Length); hoverGap = -1;
        insertionIsGap = start >= order.Length;
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
        insertionIsGap = true;
        currentPage = Math.Max(0, order.Length - 1);
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
        currentPage = insertion = index; hoverGap = -1;
        insertionIsGap = false;
        SelectionChanged?.Invoke(); Invalidate();
    }
    internal void SelectAll()
    {
        selected.Clear(); selected.UnionWith(Enumerable.Range(0, order.Length));
        anchor = insertion = currentPage; insertionIsGap = false;
        ScrollToPage(currentPage); SelectionChanged?.Invoke(); Invalidate();
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
        if (hit.Page >= 0) { SelectIndex(hit.Page, modifiers); ScrollToPage(hit.Page); }
        else if (hit.Gap >= 0)
        {
            selected.Clear(); insertion = hit.Gap; hoverGap = hit.Gap;
            insertionIsGap = true;
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
            if (hit.Gap >= 0) insertionIsGap = true;
            hoverGap = insertion; SelectionChanged?.Invoke(); Invalidate();
            AddPaste(menu, hit.Gap >= 0 ? "ここに貼り付け" : "選択した挿入位置に貼り付け", insertion);
        }
        int importIndex = hit.Page >= 0 ? hit.Page : insertion;
        menu.Items.Add(new ToolStripMenuItem("ここにPDFをインポート…", null, (_, _) => ImportRequested?.Invoke(importIndex))
        { Name = "ImportPdf", Enabled = !HasPendingCut });
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
        if (TryHandlePageMoveKey(keyData)) return true;
        if (TryHandleZoomKey(keyData)) return true;
        if (keyData == (Keys.Control | Keys.A)) { SelectAll(); return true; }
        if (TryHandlePageKey(keyData)) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    internal bool TryHandlePageMoveKey(Keys keyData)
    {
        var modifiers = keyData & Keys.Modifiers;
        var key = keyData & Keys.KeyCode;
        if (modifiers is not (Keys.None or Keys.Shift) ||
            key is not (Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown)) return false;
        CancelPageInput();
        if (order.Length == 0) return true;
        bool hadPageFocus = !insertionIsGap;
        bool backward = key is Keys.Up or Keys.PageUp;
        int next = key switch
        {
            Keys.Home => 0,
            Keys.End => order.Length - 1,
            _ => Math.Clamp(hadPageFocus ? currentPage + (backward ? -1 : 1)
                : insertion + (backward ? -1 : 0), 0, order.Length - 1)
        };
        SelectIndex(next, hadPageFocus ? modifiers : Keys.None);
        ScrollToPage(next);
        return true;
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
    protected override void OnResize(EventArgs e) { base.OnResize(e); LayoutPages(); ScrollToPage(currentPage); RequestRender(); }
    internal bool TryHandleZoomKey(Keys keyData)
    {
        var modifiers = keyData & Keys.Modifiers;
        if (modifiers is not Keys.Control && modifiers != (Keys.Control | Keys.Shift)) return false;
        var key = keyData & Keys.KeyCode;
        if (key is Keys.Oemplus or Keys.Add) { CancelPageInput(); SetZoom(zoom * 1.2f); return true; }
        if (key is Keys.OemMinus or Keys.Subtract) { CancelPageInput(); SetZoom(zoom / 1.2f); return true; }
        return false;
    }
    private void SetZoom(float value)
    {
        int centerY = -AutoScrollPosition.Y + ClientSize.Height / 2;
        int page = bounds.FindIndex(rect => rect.Bottom > centerY);
        float fraction = page < 0 ? 0 : Math.Clamp((centerY - bounds[page].Top) / (float)bounds[page].Height, 0, 1);
        zoom = Math.Clamp(value, .25f, 3f); LayoutPages();
        if (page >= 0 && page < bounds.Count)
            AutoScrollPosition = new Point(Math.Max(0, -AutoScrollPosition.X), Math.Max(0, (int)(bounds[page].Top + bounds[page].Height * fraction - ClientSize.Height / 2)));
        RequestRender(); Invalidate();
    }
    private void ScrollToPage(int index)
    {
        if (index < 0 || index >= bounds.Count) return;
        AutoScrollPosition = new Point(Math.Max(0, -AutoScrollPosition.X),
            Math.Max(0, bounds[index].Top + bounds[index].Height / 2 - ClientSize.Height / 2));
        RequestRender(); Invalidate();
    }
    internal void CenterPointOnPage(int index, float y)
    {
        if (index < 0 || index >= bounds.Count) return;
        AutoScrollPosition = new Point(Math.Max(0, -AutoScrollPosition.X),
            Math.Max(0, (int)(bounds[index].Top + bounds[index].Height * y - ClientSize.Height / 2)));
        RequestRender(); Invalidate();
    }
    private void LayoutPages()
    {
        if (sizes.Count == 0 || ClientSize.Width <= 0) return;
        float maximumWidth = Math.Max(sizes.Max(size => size.Width), order.Any(id => id >= sizes.Count) ? 595 : 0);
        int width = Math.Max(1, (int)((pixelsPerUnit is { } scaleFactor ? maximumWidth * scaleFactor
            : Math.Max(120, ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 48)) * zoom));
        if (layoutWidth != width) { layoutWidth = width; epoch++; ClearImages(); }
        float scale = width / maximumWidth;
        int HeightOf(int id) => Math.Max(1, (int)((id < sizes.Count ? sizes[id].Height : 842) * scale));
        int y = order.Length == 0 ? PageGap : Math.Max(PageGap, (ClientSize.Height - HeightOf(order[0])) / 2);
        bounds.Clear();
        foreach (int original in order)
        {
            var size = original < sizes.Count ? sizes[original] : new SizeF(595, 842);
            int w = Math.Max(1, (int)(size.Width * scale));
            int h = Math.Max(1, (int)(size.Height * scale));
            bounds.Add(new Rectangle(24 + (width - w) / 2, y, w, h)); y += h + PageGap;
        }
        int bottomPadding = order.Length == 0 ? 8 : Math.Max(8, (ClientSize.Height - HeightOf(order[^1])) / 2 - PageGap);
        AutoScrollMinSize = new Size(width + 48, Math.Max(y + bottomPadding, 80)); Invalidate();
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
            bool isSelected = ShowPageSelection && selected.Contains(i);
            using var pen = new Pen(isSelected ? Color.FromArgb(0, 100, 220) : Color.FromArgb(190, 194, 200), isSelected ? 4 : 1);
            e.Graphics.DrawRectangle(pen, rect);
            if (ShowPageSelection && i == currentPage && rect.Width > 24 && rect.Height > 24)
            {
                using var currentPen = new Pen(Color.FromArgb(220, 100, 0), 3);
                var currentRect = Rectangle.Inflate(rect, -6, -6);
                e.Graphics.DrawRectangle(currentPen, currentRect);
                var badge = new Rectangle(Math.Max(8, rect.Left + 12), Math.Max(8, rect.Top + 12),
                    Math.Min(rect.Width - 24, Font.Height * 12), Font.Height + 12);
                using var badgeBrush = new SolidBrush(Color.FromArgb(220, 100, 0));
                e.Graphics.FillRectangle(badgeBrush, badge);
                TextRenderer.DrawText(e.Graphics, $"現在のページ: {i + 1}", Font, badge, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            if (hoverGap != i)
                TextRenderer.DrawText(e.Graphics, $"{i + 1} ページ" + (isSelected ? "  ✓ 選択中" : ""), Font, new Rectangle(rect.Left, rect.Top - Font.Height - 4, rect.Width, Font.Height + 4),
                    isSelected ? Color.FromArgb(0, 80, 180) : Color.DimGray, TextFormatFlags.Left);
        }
        if (bounds.Count == 0)
            TextRenderer.DrawText(e.Graphics, HasPendingCut ? "すべてのページを切り取り中です。右クリックで貼り付けてください。"
                : "PDFを準備しています…",
                Font, ClientRectangle, Color.DimGray, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        if (hoverGap >= 0 && hoverGap <= bounds.Count && sourcesReady)
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
