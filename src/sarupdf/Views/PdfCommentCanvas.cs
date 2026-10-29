using SaruPdf.Documents;

namespace SaruPdf.Views;

internal sealed class PdfCommentCanvas : PdfPageCanvas
{
    private PdfCaretSelection? selection;
    private readonly System.Windows.Forms.Timer blink = new() { Interval = 500 };
    private readonly System.Windows.Forms.Timer dragScroll = new() { Interval = 50 };
    private bool caretVisible = true;
    private readonly Dictionary<Guid, PdfCommentLabel> labels = [];
    private readonly Dictionary<Guid, PdfCommentMark> labelVersions = [];
    private bool dragging, dragMoved;
    private Point dragOrigin;
    private Point lastDragPoint;
    private PdfCommentMark? pressedComment;
    private string? pressedReadOnlyComment;
    private ContextMenuStrip? commentMenu;
    internal PdfCaretSelection? Selection => selection;
    internal IReadOnlyList<PdfCommentMark> Comments { get; private set; } = [];
    internal event Action? CommentRequested;
    internal event Action<string>? ExistingCommentRequested;
    internal event Action<Exception>? CommandFailed;
    internal event Action<PdfCommentMark>? EditCommentRequested;

    internal PdfCommentCanvas()
    {
        ShowPageSelection = false; AccessibleName = "PDFコメント画面";
        Cursor = Cursors.IBeam;
        blink.Tick += (_, _) => { caretVisible = !caretVisible; Invalidate(); };
        blink.Start();
        dragScroll.Tick += (_, _) => { if (dragging && (lastDragPoint.Y < 12 || lastDragPoint.Y > ClientSize.Height - 12)) ContinueTextDrag(lastDragPoint); };
    }
    internal void SetTextMap(PdfTextMap map)
    {
        selection = new PdfCaretSelection(map);
        if (CurrentPage > 0)
        {
            int first = map.Glyphs.FindIndex(g => g.Page >= CurrentPage);
            if (first >= 0) selection.Set(first, false);
        }
        FollowCaret(); Invalidate();
    }
    internal void UpdateComments(IReadOnlyList<PdfCommentMark> comments)
    {
        Comments = comments;
        foreach (var id in labels.Keys.Where(id => !comments.Any(c => c.Id == id && labelVersions.GetValueOrDefault(id) == c)).ToArray())
        { labels[id].Dispose(); labels.Remove(id); labelVersions.Remove(id); }
        if (selection is not null)
            foreach (var comment in comments.Where(c => !labels.ContainsKey(c.Id)))
            {
                labels.Add(comment.Id, new PdfCommentLabel(selection.Map.Pages[comment.PageId], comment.Quads, comment.Text));
                labelVersions[comment.Id] = comment;
            }
        Invalidate();
    }
    internal bool TryHandleCaretKey(Keys keyData)
    {
        var key = keyData & Keys.KeyCode; var modifiers = keyData & Keys.Modifiers;
        if (selection is null) return key is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Enter;
        if (keyData == (Keys.Control | Keys.A)) { selection.SelectAll(); FollowCaret(); return true; }
        if (keyData == (Keys.Control | Keys.C))
        {
            try { if (selection.HasSelection) Clipboard.SetText(selection.SelectedText); }
            catch (System.Runtime.InteropServices.ExternalException ex) { CommandFailed?.Invoke(ex); }
            return true;
        }
        if (modifiers == Keys.None && key == Keys.Escape) { selection.Set(selection.Caret, false); Invalidate(); return true; }
        if (modifiers == Keys.None && key == Keys.Enter)
        {
            if (selection.HasSelection && selection.Regions().Count > 0) CommentRequested?.Invoke();
            return true;
        }
        if ((modifiers & (Keys.Alt | Keys.LWin | Keys.RWin)) != 0 ||
            key is not (Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown)) return false;
        if ((modifiers & Keys.Control) != 0 && key is not (Keys.Home or Keys.End)) return false;
        selection.Move(key, (modifiers & Keys.Shift) != 0, (modifiers & Keys.Control) != 0);
        FollowCaret(); return true;
    }
    private void FollowCaret()
    {
        caretVisible = true;
        if (selection?.Current is { } glyph)
        {
            SelectPage(glyph.Page + 1);
            CenterPointOnPage(glyph.Page, glyph.Bounds.Top + glyph.Bounds.Height / 2);
        }
        Invalidate();
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (TryHandleCaretKey(keyData)) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        if (selection is null) return;
        if (e.Button == MouseButtons.Right)
        {
            commentMenu?.Dispose(); commentMenu = CreateCommentMenu(e.Location); commentMenu.Show(this, e.Location); return;
        }
        if (e.Button == MouseButtons.Left) BeginTextDrag(e.Location, ModifierKeys);
    }
    internal ContextMenuStrip CreateCommentMenu(Point point)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("コメントを追加(&A)", null, (_, _) => CommentRequested?.Invoke())
        { Name = "AddComment", Enabled = selection is { HasSelection: true } && selection.Regions().Count > 0 });
        if (CommentAt(point) is { } comment)
            menu.Items.Add(new ToolStripMenuItem("コメントを編集(&E)", null, (_, _) => EditCommentRequested?.Invoke(comment)) { Name = "EditComment" });
        return menu;
    }
    private PdfCommentMark? CommentAt(Point point)
    {
        if (selection is null) return null;
        foreach (var comment in Comments.Where(c => c.PageId < PageCount))
        {
            var bounds = PageBounds(comment.PageId);
            var normalized = new PointF((point.X - bounds.Left) / (float)bounds.Width, (point.Y - bounds.Top) / (float)bounds.Height);
            if (comment.Quads.Any(q => selection.Map.Pages[comment.PageId].Rectangle(q).Contains(normalized)) ||
                (labels.TryGetValue(comment.Id, out var label) && label.NormalizedBounds.Contains(normalized))) return comment;
        }
        return null;
    }
    private int? TextAt(Point point)
    {
        if (selection is null) return null;
        for (int page = 0; page < PageCount; page++)
        {
            var bounds = PageBounds(page); if (point.Y < bounds.Top || point.Y > bounds.Bottom) continue;
            var normalized = new PointF((point.X - bounds.Left) / (float)bounds.Width, (point.Y - bounds.Top) / (float)bounds.Height);
            var candidates = selection.Map.Glyphs.Select((g, i) => (g, i)).Where(item => item.g.Page == page).ToArray();
            if (candidates.Length == 0) return null;
            var closest = candidates.MinBy(item => Math.Abs(item.g.Bounds.Top + item.g.Bounds.Height / 2 - normalized.Y) * 4 + Math.Abs(item.g.Bounds.Left - normalized.X));
            return closest.i + (normalized.X > closest.g.Bounds.Left + closest.g.Bounds.Width / 2 ? 1 : 0);
        }
        return null;
    }
    internal void BeginTextDrag(Point point, Keys modifiers)
    {
        if (selection is null || TextAt(point) is not { } position) return;
        dragging = true; dragMoved = false; dragOrigin = lastDragPoint = point; Capture = true; dragScroll.Start();
        pressedComment = modifiers == Keys.None ? CommentAt(point) : null;
        pressedReadOnlyComment = null;
        if (modifiers == Keys.None && pressedComment is null)
            foreach (var comment in selection.Map.ExistingComments)
                if (Screen(comment.Page, comment.Bounds).Contains(point)) { pressedReadOnlyComment = comment.Text; break; }
        selection.Set(position, (modifiers & Keys.Shift) != 0); caretVisible = true; Invalidate();
    }
    internal void ContinueTextDrag(Point point)
    {
        if (!dragging || selection is null) return;
        lastDragPoint = point;
        dragMoved |= Math.Abs(point.X - dragOrigin.X) > 3 || Math.Abs(point.Y - dragOrigin.Y) > 3;
        if (!dragMoved) return;
        if (point.Y < 12 || point.Y > ClientSize.Height - 12)
            AutoScrollPosition = new Point(Math.Max(0, -AutoScrollPosition.X), Math.Max(0, -AutoScrollPosition.Y + (point.Y < 12 ? -24 : 24)));
        if (TextAt(point) is { } position) selection.Set(position, true);
        caretVisible = true; Invalidate();
    }
    internal void EndTextDrag(Point point)
    {
        if (!dragging) return;
        ContinueTextDrag(point); dragging = false; dragScroll.Stop(); Capture = false;
        if (!dragMoved)
        {
            if (pressedComment is not null) EditCommentRequested?.Invoke(pressedComment);
            else if (pressedReadOnlyComment is not null) ExistingCommentRequested?.Invoke(pressedReadOnlyComment);
        }
        pressedComment = null; pressedReadOnlyComment = null; Invalidate();
    }
    protected override void OnMouseMove(MouseEventArgs e) => ContinueTextDrag(e.Location);
    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) EndTextDrag(e.Location);
    }
    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e); if (!Capture) { dragging = false; dragScroll.Stop(); }
    }
    private RectangleF Screen(int page, RectangleF normalized)
    {
        var rect = PageBounds(page);
        return new(rect.Left + rect.Width * normalized.Left, rect.Top + rect.Height * normalized.Top, rect.Width * normalized.Width, rect.Height * normalized.Height);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (selection is null) return;
        using var yellow = new SolidBrush(Color.FromArgb(90, 255, 205, 0));
        foreach (var comment in Comments.Where(c => c.PageId < PageCount))
        foreach (var quad in comment.Quads) e.Graphics.FillRectangle(yellow, Screen(comment.PageId, selection.Map.Pages[comment.PageId].Rectangle(quad)));
        using var selected = new SolidBrush(Color.FromArgb(90, 30, 125, 245));
        foreach (var glyph in selection.Selected.Where(g => g.Text != "\n")) e.Graphics.FillRectangle(selected, Screen(glyph.Page, glyph.Bounds));
        foreach (var comment in Comments.Where(c => c.PageId < PageCount))
            if (labels.TryGetValue(comment.Id, out var label) && Screen(comment.PageId, label.NormalizedBounds).IntersectsWith(ClientRectangle))
                label.Draw(e.Graphics, PageBounds(comment.PageId));
        if (caretVisible && selection.Current is { } current)
        {
            var rect = Screen(current.Page, current.Bounds);
            float x = selection.Caret == selection.Map.Glyphs.Count ? rect.Right : rect.Left;
            using var pen = new Pen(Color.FromArgb(0, 110, 65), 2);
            e.Graphics.DrawLine(pen, x, rect.Top - 2, x, rect.Bottom + 2);
        }
        if (selection.Map.Glyphs.Count == 0)
            TextRenderer.DrawText(e.Graphics, "文字情報がないPDFです。画像だけのページではキャレット選択できません。", Font,
                new Rectangle(12, 12, Math.Max(1, ClientSize.Width - 24), 60), Color.DarkGreen, Color.White, TextFormatFlags.WordBreak);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { blink.Dispose(); dragScroll.Dispose(); commentMenu?.Dispose(); foreach (var label in labels.Values) label.Dispose(); labels.Clear(); labelVersions.Clear(); }
        base.Dispose(disposing);
    }
}
