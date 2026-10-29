using System.Globalization;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using PdfSharp.Pdf.IO;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace SaruPdf.Documents;

internal sealed record PdfTextGlyph(string Text, int Page, int Line, RectangleF Bounds);
internal sealed record PdfExistingComment(int Page, RectangleF Bounds, string Text);
internal sealed record PdfTextPage(double Width, double Height, double Left, double Bottom, double CropWidth, double CropHeight, int Rotation)
{
    internal PdfPointValue ToPdf(double x, double y) => Rotation switch
    {
        90 => new(Left + CropWidth - y, Bottom + x),
        180 => new(Left + CropWidth - x, Bottom + CropHeight - y),
        270 => new(Left + y, Bottom + CropHeight - x),
        _ => new(Left + x, Bottom + y)
    };
    internal PointF FromPdf(double x, double y)
    {
        (double dx, double dy) = Rotation switch
        {
            90 => (y - Bottom, CropWidth - (x - Left)),
            180 => (CropWidth - (x - Left), CropHeight - (y - Bottom)),
            270 => (CropHeight - (y - Bottom), x - Left),
            _ => (x - Left, y - Bottom)
        };
        return new((float)(dx / Width), (float)(1 - dy / Height));
    }
    internal PdfCommentQuad Quad(RectangleF normalized)
    {
        double left = normalized.Left * Width, right = normalized.Right * Width;
        double bottom = (1 - normalized.Bottom) * Height, top = (1 - normalized.Top) * Height;
        return new(ToPdf(left, bottom), ToPdf(right, bottom), ToPdf(right, top), ToPdf(left, top));
    }
    internal RectangleF Rectangle(PdfCommentQuad quad)
    {
        var points = quad.Points.Select(p => FromPdf(p.X, p.Y)).ToArray();
        return RectangleF.FromLTRB(points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y));
    }
}

internal sealed class PdfTextMap
{
    internal List<PdfTextPage> Pages { get; } = [];
    internal List<PdfTextGlyph> Glyphs { get; } = [];
    internal List<PdfExistingComment> ExistingComments { get; } = [];

    internal static PdfTextMap Read(string path)
    {
        var result = new PdfTextMap();
        using (var pdf = PigDocument.Open(path))
        {
            int lineId = 0;
            foreach (var page in pdf.GetPages())
            {
                var crop = page.CropBox.Bounds;
                result.Pages.Add(new(page.Width, page.Height, crop.Left, crop.Bottom, crop.Width, crop.Height, page.Rotation.Value));
                var words = page.GetWords(NearestNeighbourWordExtractor.Instance);
                var blocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);
                var ordered = UnsupervisedReadingOrderDetector.Instance.Get(blocks);
                foreach (var block in ordered)
                foreach (var line in block.TextLines)
                {
                    UglyToad.PdfPig.Content.Letter? previous = null;
                    foreach (var word in line.Words)
                    foreach (var letter in word.Letters)
                    {
                        var rectangle = letter.GlyphRectangleLoose;
                        var corners = new[] { rectangle.BottomLeft, rectangle.BottomRight, rectangle.TopLeft, rectangle.TopRight };
                        double minX = corners.Min(p => p.X), maxX = corners.Max(p => p.X), minY = corners.Min(p => p.Y), maxY = corners.Max(p => p.Y);
                        float left = (float)(minX / page.Width), top = (float)(1 - maxY / page.Height);
                        float width = (float)((maxX - minX) / page.Width), height = (float)((maxY - minY) / page.Height);
                        if (width <= 0 || height <= 0 || left > 1 || top > 1 || left + width < 0 || top + height < 0) continue;
                        if (previous is not null && !string.IsNullOrWhiteSpace(previous.Value) && !string.IsNullOrWhiteSpace(letter.Value))
                        {
                            double dx = letter.StartBaseLine.X - previous.EndBaseLine.X, dy = letter.StartBaseLine.Y - previous.EndBaseLine.Y;
                            if (Math.Sqrt(dx * dx + dy * dy) > Math.Max(1, Math.Abs(letter.FontSize) * .15))
                            {
                                var prior = result.Glyphs[^1].Bounds;
                                var gap = RectangleF.FromLTRB(Math.Min(prior.Right, left), Math.Min(prior.Top, top),
                                    Math.Max(prior.Right, left), Math.Max(prior.Bottom, top + height));
                                result.Glyphs.Add(new(" ", page.Number - 1, lineId, gap));
                            }
                        }
                        var elements = StringInfo.GetTextElementEnumerator(letter.Value);
                        List<string> text = []; while (elements.MoveNext()) text.Add(elements.GetTextElement());
                        for (int i = 0; i < text.Count; i++)
                            result.Glyphs.Add(new(text[i], page.Number - 1, lineId,
                                new RectangleF(left + width * i / text.Count, top, width / text.Count, height)));
                        previous = letter;
                    }
                    if (result.Glyphs.LastOrDefault() is { } last && last.Line == lineId)
                        result.Glyphs.Add(new("\n", page.Number - 1, lineId, new RectangleF(last.Bounds.Right, last.Bounds.Top, .001f, last.Bounds.Height)));
                    lineId++;
                }
            }
        }
        using var source = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        for (int p = 0; p < source.PageCount; p++)
        for (int a = 0; a < source.Pages[p].Annotations.Count; a++)
        {
            var annotation = source.Pages[p].Annotations[a];
            string text = annotation.Elements.GetString("/Contents");
            if (text.Length == 0 || annotation.Elements.GetName("/Subtype") == "/Popup") continue;
            if (annotation.Elements.GetArray("/QuadPoints") is { } quads && quads.Elements.Count >= 8)
            {
                static double Number(PdfSharp.Pdf.PdfItem item) => item switch
                {
                    PdfSharp.Pdf.PdfReal real => real.Value,
                    PdfSharp.Pdf.PdfInteger integer => integer.Value,
                    _ => double.NaN
                };
                for (int q = 0; q + 7 < quads.Elements.Count; q += 8)
                {
                    var corners = Enumerable.Range(0, 4).Select(i => result.Pages[p].FromPdf(Number(quads.Elements[q + 2 * i]), Number(quads.Elements[q + 2 * i + 1]))).ToArray();
                    if (corners.Any(point => !float.IsFinite(point.X) || !float.IsFinite(point.Y))) continue;
                    result.ExistingComments.Add(new(p, RectangleF.FromLTRB(corners.Min(a => a.X), corners.Min(a => a.Y), corners.Max(a => a.X), corners.Max(a => a.Y)), text));
                }
                continue;
            }
            var rect = annotation.Elements.GetRectangle("/Rect");
            var page = result.Pages[p];
            var points = new[] { page.FromPdf(rect.X1, rect.Y1), page.FromPdf(rect.X2, rect.Y2) };
            result.ExistingComments.Add(new(p, RectangleF.FromLTRB(points.Min(a => a.X), points.Min(a => a.Y), points.Max(a => a.X), points.Max(a => a.Y)), text));
        }
        return result;
    }
}

internal sealed class PdfCaretSelection(PdfTextMap map)
{
    internal int Caret { get; private set; }
    internal int Anchor { get; private set; }
    internal int Start => Math.Min(Caret, Anchor);
    internal int End => Math.Max(Caret, Anchor);
    internal bool HasSelection => Start != End;
    internal PdfTextMap Map => map;
    internal PdfTextGlyph? Current => map.Glyphs.Count == 0 ? null : map.Glyphs[Math.Min(Caret, map.Glyphs.Count - 1)];
    internal IEnumerable<PdfTextGlyph> Selected => map.Glyphs.Skip(Start).Take(End - Start);
    internal string SelectedText => string.Concat(Selected.Select(g => g.Text)).ReplaceLineEndings("\r\n");

    internal void Set(int position, bool extend)
    {
        Caret = Math.Clamp(position, 0, map.Glyphs.Count);
        if (!extend) Anchor = Caret;
    }
    internal void SelectAll() { Anchor = 0; Caret = map.Glyphs.Count; }
    internal void Move(Keys key, bool extend, bool control = false)
    {
        if (Current is not { } glyph) return;
        int next = Caret;
        var line = map.Glyphs.Select((g, i) => (g, i)).Where(item => item.g.Page == glyph.Page && item.g.Line == glyph.Line).ToArray();
        switch (key)
        {
            case Keys.Left: next = !extend && HasSelection ? Start : Caret - 1; break;
            case Keys.Right: next = !extend && HasSelection ? End : Caret + 1; break;
            case Keys.Home: next = control ? 0 : line[0].i; break;
            case Keys.End: next = control ? map.Glyphs.Count : line[^1].i; break;
            case Keys.PageUp:
            case Keys.PageDown:
                int page = glyph.Page + (key == Keys.PageUp ? -1 : 1);
                var first = map.Glyphs.FindIndex(g => key == Keys.PageUp ? g.Page >= page && g.Page < glyph.Page : g.Page >= page);
                next = first >= 0 ? first : key == Keys.PageUp ? 0 : map.Glyphs.Count;
                break;
            case Keys.Up:
            case Keys.Down:
                int adjacent = glyph.Line + (key == Keys.Up ? -1 : 1);
                var candidates = map.Glyphs.Select((g, i) => (g, i)).Where(item => item.g.Line == adjacent).ToArray();
                if (candidates.Length > 0) next = candidates.MinBy(item => Math.Abs(item.g.Bounds.Left - glyph.Bounds.Left)).i;
                break;
        }
        Set(next, extend);
    }
    internal IReadOnlyList<PdfCommentRegion> Regions() => Selected.Where(g => !string.IsNullOrWhiteSpace(g.Text)).GroupBy(g => g.Page)
        .Select(page => new PdfCommentRegion(page.Key, page.GroupBy(g => g.Line)
            .Select(line => map.Pages[page.Key].Quad(line.Select(g => g.Bounds).Aggregate(RectangleF.Union))).ToArray())).ToArray();
}
