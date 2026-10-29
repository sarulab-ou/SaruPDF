using SaruPdf.Documents;

namespace SaruPdf.Views;

internal sealed class PdfCommentCanvas : PdfPageCanvas
{
    private PdfCaretSelection? selection;
    private readonly System.Windows.Forms.Timer blink = new() { Interval = 500 };
    private bool caretVisible = true;
    private readonly Dictionary<Guid, PdfCommentLabel> labels = [];
    internal PdfCaretSelection? Selection => selection;
    internal IReadOnlyList<PdfCommentMark> Comments { get; private set; } = [];
    internal event Action? CommentRequested;
    internal event Action<string>? ExistingCommentRequested;
    internal event Action<Exception>? CommandFailed;

    internal PdfCommentCanvas()
    {
        ShowPageSelection = false; AccessibleName = "PDFコメント画面";
        Cursor = Cursors.IBeam;
        blink.Tick += (_, _) => { caretVisible = !caretVisible; Invalidate(); };
        blink.Start();
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
        foreach (var id in labels.Keys.Where(id => !comments.Any(c => c.Id == id)).ToArray()) { labels[id].Dispose(); labels.Remove(id); }
        if (selection is not null)
            foreach (var comment in comments.Where(c => !labels.ContainsKey(c.Id)))
                labels.Add(comment.Id, new PdfCommentLabel(selection.Map.Pages[comment.PageId], comment.Quads, comment.Text));
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
        if (selection is null || e.Button != MouseButtons.Left) return;
        for (int page = 0; page < PageCount; page++)
        {
            var bounds = PageBounds(page); if (!bounds.Contains(e.Location)) continue;
            var point = new PointF((e.X - bounds.Left) / (float)bounds.Width, (e.Y - bounds.Top) / (float)bounds.Height);
            if (ModifierKeys == Keys.None)
            {
                string? text = selection.Map.ExistingComments.FirstOrDefault(c => c.Page == page && c.Bounds.Contains(point))?.Text;
                text ??= Comments.FirstOrDefault(c => c.PageId == page && (c.Quads.Any(q => selection.Map.Pages[page].Rectangle(q).Contains(point)) ||
                    (labels.TryGetValue(c.Id, out var label) && label.NormalizedBounds.Contains(point))))?.Text;
                if (text is not null) { ExistingCommentRequested?.Invoke(text); return; }
            }
            var candidates = selection.Map.Glyphs.Select((g, i) => (g, i)).Where(item => item.g.Page == page).ToArray();
            if (candidates.Length == 0) return;
            var closest = candidates.MinBy(item => Math.Abs(item.g.Bounds.Top + item.g.Bounds.Height / 2 - point.Y) * 4 + Math.Abs(item.g.Bounds.Left - point.X));
            selection.Set(closest.i + (point.X > closest.g.Bounds.Left + closest.g.Bounds.Width / 2 ? 1 : 0), (ModifierKeys & Keys.Shift) != 0);
            FollowCaret(); return;
        }
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
        if (disposing) { blink.Dispose(); foreach (var label in labels.Values) label.Dispose(); labels.Clear(); }
        base.Dispose(disposing);
    }
}
