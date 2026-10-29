using System.Drawing.Drawing2D;

namespace SaruPdf.Documents;

// One layout supplies both the unsaved overlay and the PDF annotation appearance.
// Coordinates and vector glyph outlines are in displayed page units, top down.
internal sealed class PdfCommentLabel : IDisposable
{
    internal const float FontSize = 11;
    private static readonly Lazy<string> fontFamilyName = new(FindFontFamily);
    internal static string FontFamilyName => fontFamilyName.Value;
    internal RectangleF Bounds { get; }
    internal GraphicsPath Outline { get; }
    internal PdfTextPage Page { get; }
    internal RectangleF NormalizedBounds => new(Bounds.X / (float)Page.Width, Bounds.Y / (float)Page.Height,
        Bounds.Width / (float)Page.Width, Bounds.Height / (float)Page.Height);

    internal PdfCommentLabel(PdfTextPage page, IReadOnlyList<PdfCommentQuad> quads, string text)
    {
        Page = page;
        var target = page.Rectangle(quads[0]);
        float margin = Math.Min(6, (float)Math.Min(page.Width, page.Height) / 20);
        float maxWidth = Math.Max(1, Math.Min(280, (float)page.Width - margin * 2));
        float maxHeight = Math.Max(1, Math.Min(200, (float)page.Height - margin * 2));
        using var family = new FontFamily(FontFamilyName);
        using var font = new Font(family, FontSize, FontStyle.Regular, GraphicsUnit.Pixel);
        using var bitmap = new Bitmap(1, 1);
        using var graphics = Graphics.FromImage(bitmap);
        using var format = (StringFormat)StringFormat.GenericTypographic.Clone();
        format.FormatFlags |= StringFormatFlags.LineLimit;
        format.Trimming = StringTrimming.EllipsisCharacter;
        text = text.ReplaceLineEndings("\n");
        var measured = graphics.MeasureString(text, font, new SizeF(Math.Max(1, maxWidth - 4), 100000), format);
        float width = Math.Min(maxWidth, Math.Max(16, (float)Math.Ceiling(measured.Width) + 4));
        float height = Math.Min(maxHeight, Math.Max(16, (float)Math.Ceiling(measured.Height) + 2));
        float left = Math.Clamp(target.Left * (float)page.Width + 1, margin, Math.Max(margin, (float)page.Width - width - margin));
        float overlap = Math.Min(6, target.Height * (float)page.Height * .5f);
        float top = Math.Clamp(target.Top * (float)page.Height - height + overlap, margin,
            Math.Max(margin, (float)page.Height - height - margin));
        Bounds = new(left, top, width, height);
        Outline = new GraphicsPath();
        try { Outline.AddString(text, family, (int)FontStyle.Regular, FontSize, new RectangleF(2, 0, Math.Max(1, width - 4), height), format); }
        catch { Outline.Dispose(); throw; }
        if (Outline.PointCount > 0)
        {
            // Align the actual glyph ink, rather than the font's descent space,
            // so the red letters themselves overlap the highlight slightly.
            float inkBottom = Outline.GetBounds().Bottom;
            top = Math.Clamp(target.Top * (float)page.Height - inkBottom + Math.Min(3, target.Height * (float)page.Height * .25f),
                margin, Math.Max(margin, (float)page.Height - height - margin));
            Bounds = new(left, top, width, height);
        }
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
