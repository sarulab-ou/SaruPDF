using System.Drawing.Drawing2D;
using System.Globalization;

namespace SaruPdf.Documents;

// One layout supplies both the unsaved overlay and the PDF annotation appearance.
// Coordinates and vector glyph outlines are in displayed page units, top down.
internal sealed class PdfCommentLabel : IDisposable
{
    internal const float FontSize = 7;
    private static readonly Lazy<string> fontFamilyName = new(FindFontFamily);
    internal static string FontFamilyName => fontFamilyName.Value;
    internal RectangleF Bounds { get; }
    internal GraphicsPath Outline { get; }
    internal PdfTextPage Page { get; }
    internal RectangleF NormalizedBounds => new(Bounds.X / (float)Page.Width, Bounds.Y / (float)Page.Height,
        Bounds.Width / (float)Page.Width, Bounds.Height / (float)Page.Height);

    internal PdfCommentLabel(PdfTextPage page, IReadOnlyList<PdfCommentQuad> quads, string text, PdfCommentQuad? paragraph = null)
    {
        Page = page;
        var targets = quads.Select(page.Rectangle).OrderBy(rect => rect.Top).ThenBy(rect => rect.Left).ToArray();
        var target = targets[0];
        float margin = Math.Min(6, (float)Math.Min(page.Width, page.Height) / 20);
        var column = paragraph is null ? RectangleF.Empty : page.Rectangle(paragraph);
        float first = Math.Clamp(target.Left * (float)page.Width, 0, Math.Max(0, (float)page.Width - FontSize * 1.5f));
        float left = paragraph is null ? first : Math.Clamp(column.Left * (float)page.Width, 0, first);
        float right = paragraph is null ? (float)page.Width : Math.Clamp(Math.Max(column.Right, targets.Max(rect => rect.Right)) * (float)page.Width, first, (float)page.Width);
        if (right - left < FontSize * 1.5f) { left = first; right = (float)page.Width; }
        first = Math.Min(first, Math.Max(left, right - FontSize * 1.5f));
        float maxHeight = Math.Max(1, Math.Min(200, (float)page.Height - margin * 2));
        using var family = new FontFamily(FontFamilyName);
        using var font = new Font(family, FontSize, FontStyle.Regular, GraphicsUnit.Pixel);
        using var bitmap = new Bitmap(1, 1);
        using var graphics = Graphics.FromImage(bitmap);
        using var format = (StringFormat)StringFormat.GenericTypographic.Clone();
        format.FormatFlags |= StringFormatFlags.LineLimit | StringFormatFlags.MeasureTrailingSpaces;
        format.Trimming = StringTrimming.None;
        text = text.ReplaceLineEndings("\n");
        float lineHeight = font.GetHeight(graphics);
        var elements = new List<string>();
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext()) elements.Add(enumerator.GetTextElement());
        var lines = new List<(string Text, float X, float Anchor, float Overlap)>();
        var differences = targets.Zip(targets.Skip(1), (a, b) => (b.Top - a.Top) * (float)page.Height).Where(d => d > lineHeight * .5f).Order().ToArray();
        float advance = differences.Length > 0 ? differences[differences.Length / 2] : Math.Max(lineHeight, target.Height * (float)page.Height * 1.2f);
        int position = 0, limit = Math.Max(1, (int)((maxHeight - 2) / lineHeight));
        while (position < elements.Count && lines.Count < limit)
        {
            int index = lines.Count;
            var row = targets[Math.Min(index, targets.Length - 1)];
            float x = index < targets.Length ? Math.Clamp(row.Left * (float)page.Width, left, Math.Max(left, right - FontSize * 1.5f)) : left;
            if (index == 0) x = first;
            float anchor = row.Top * (float)page.Height + Math.Max(0, index - targets.Length + 1) * advance;
            if (anchor > (float)page.Height - 1 || anchor - target.Top * (float)page.Height > maxHeight) break;
            float available = Math.Max(1, right - x - 2);
            int start = position, lastSpace = -1;
            string current = "";
            while (position < elements.Count && elements[position] != "\n")
            {
                string candidate = current + elements[position];
                float measured = graphics.MeasureString(candidate, font, PointF.Empty, format).Width;
                if (current.Length > 0 && measured > available) break;
                current = candidate;
                if (string.IsNullOrWhiteSpace(elements[position])) lastSpace = position;
                position++;
            }
            if (position < elements.Count && elements[position] != "\n" && lastSpace >= start && lastSpace < position - 1)
            {
                position = lastSpace + 1;
                current = string.Concat(elements.Skip(start).Take(lastSpace - start));
            }
            lines.Add((current, x, anchor, Math.Min(3, row.Height * (float)page.Height * .25f)));
            if (position < elements.Count && elements[position] == "\n") position++;
            else while (position < elements.Count && elements[position] != "\n" && string.IsNullOrWhiteSpace(elements[position])) position++;
        }
        Outline = new GraphicsPath();
        try
        {
            for (int i = 0; i < lines.Count; i++)
            {
                using var line = new GraphicsPath();
                line.AddString(lines[i].Text, family, (int)FontStyle.Regular, FontSize,
                    new RectangleF(0, 0, Math.Max(1, right - lines[i].X - 2), lineHeight + 2), format);
                if (line.PointCount == 0) continue;
                using var align = new Matrix();
                var ink = line.GetBounds();
                float top = Math.Max(0, Math.Min((float)page.Height - 1, lines[i].Anchor + lines[i].Overlap) - ink.Bottom);
                align.Translate(lines[i].X - ink.Left, top);
                line.Transform(align); Outline.AddPath(line, false);
            }
        }
        catch { Outline.Dispose(); throw; }
        if (Outline.PointCount > 0)
        {
            var ink = Outline.GetBounds();
            float boundsLeft = Math.Clamp(ink.Left, 0, (float)page.Width - 1);
            float boundsTop = Math.Clamp(ink.Top, 0, (float)page.Height - 1);
            Bounds = RectangleF.FromLTRB(boundsLeft, boundsTop, Math.Min((float)page.Width, Math.Max(boundsLeft + 1, ink.Right + 1)),
                Math.Min((float)page.Height, Math.Max(boundsTop + 1, ink.Bottom + 1)));
            using var local = new Matrix(); local.Translate(-boundsLeft, -boundsTop); Outline.Transform(local);
        }
        else Bounds = new RectangleF(first, Math.Clamp(target.Top * (float)page.Height, 0, (float)page.Height - 1), 1, 1);
    }

    internal PdfCommentQuad PdfBounds() => Page.Quad(NormalizedBounds);

    private static string FindFontFamily()
    {
        foreach (string name in new[] { "Yu Mincho Light", "游明朝 Light", "Yu Mincho", "游明朝", "MS PMincho", "ＭＳ Ｐ明朝" })
        {
            try
            {
                using var family = new FontFamily(name);
                if (family.IsStyleAvailable(FontStyle.Regular)) return family.GetName(0x0409);
            }
            catch (ArgumentException) { }
        }
        return FontFamily.GenericSerif.Name;
    }

    internal void Draw(Graphics graphics, Rectangle pageBounds)
    {
        float sx = pageBounds.Width / (float)Page.Width, sy = pageBounds.Height / (float)Page.Height;
        using var matrix = new Matrix(sx, 0, 0, sy, pageBounds.Left + Bounds.Left * sx, pageBounds.Top + Bounds.Top * sy);
        using var path = (GraphicsPath)Outline.Clone(); path.Transform(matrix);
        var state = graphics.Save();
        try
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.FillPath(Brushes.Red, path);
        }
        finally { graphics.Restore(state); }
    }

    public void Dispose() => Outline.Dispose();
}
