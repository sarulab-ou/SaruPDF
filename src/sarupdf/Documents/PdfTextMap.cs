using System.Globalization;
using UglyToad.PdfPig.DocumentLayoutAnalysis;
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
    internal static PdfTextPage FromPdfPage(PdfSharp.Pdf.PdfPage page)
    {
        // Writable box getters create absent entries. Reading geometry must not
        // insert an empty CropBox, which can hide the original page in readers.
        var crop = page.EffectiveCropBoxReadOnly; var media = page.MediaBoxReadOnly;
        if (crop.Width <= 0 || crop.Height <= 0) crop = media;
        double left = Math.Max(crop.X1, media.X1), bottom = Math.Max(crop.Y1, media.Y1);
        double right = Math.Min(crop.X2, media.X2), top = Math.Min(crop.Y2, media.Y2);
        if (right <= left || top <= bottom) { left = crop.X1; bottom = crop.Y1; right = crop.X2; top = crop.Y2; }
        int rotation = ((page.Rotate % 360) + 360) % 360;
        double width = right - left, height = top - bottom;
        return new(rotation is 90 or 270 ? height : width, rotation is 90 or 270 ? width : height, left, bottom, width, height, rotation);
    }
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
                if (page.Rotation.Value == 0) blocks = SplitMixedColumns(blocks, page.Width);
                IEnumerable<TextBlock> ordered = UnsupervisedReadingOrderDetector.Instance.Get(blocks);
                if (page.Rotation.Value == 0) ordered = OrderColumns(ordered.ToArray(), page.Width);
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

    // Docstrum can merge both columns into one block, and even one TextLine.
    // Split words only when a wide block has a clear gutter and substantial,
    // vertically overlapping text on both sides; leave normal headings intact.
    private static IReadOnlyList<TextBlock> SplitMixedColumns(IReadOnlyList<TextBlock> blocks, double pageWidth)
    {
        List<TextBlock> result = [];
        foreach (var block in blocks)
        {
            if (block.TextLines.Count < 8 || block.BoundingBox.Width < pageWidth * .7) { result.Add(block); continue; }
            var words = block.TextLines.SelectMany(line => line.Words).ToArray();
            double? split = null, bestDistance = double.MaxValue;
            for (int step = 150; step <= 350; step++)
            {
                double x = pageWidth * step / 500.0;
                if (words.Any(word => word.BoundingBox.Left < x && word.BoundingBox.Right > x)) continue;
                var left = block.TextLines.Where(line => line.Words.Any(word => word.BoundingBox.Right <= x)).ToArray();
                var right = block.TextLines.Where(line => line.Words.Any(word => word.BoundingBox.Left >= x)).ToArray();
                if (left.Length < 4 || right.Length < 4) continue;
                var leftWords = words.Where(word => word.BoundingBox.Right <= x).ToArray();
                var rightWords = words.Where(word => word.BoundingBox.Left >= x).ToArray();
                double overlap = Math.Min(leftWords.Max(word => word.BoundingBox.Top), rightWords.Max(word => word.BoundingBox.Top)) -
                                 Math.Max(leftWords.Min(word => word.BoundingBox.Bottom), rightWords.Min(word => word.BoundingBox.Bottom));
                if (overlap < pageWidth * .08) continue;
                double distance = Math.Abs(x - pageWidth / 2);
                if (distance < bestDistance) { bestDistance = distance; split = x; }
            }
            if (split is not { } column) { result.Add(block); continue; }
            foreach (bool left in new[] { true, false })
            {
                var lines = block.TextLines.Select(line => line.Words.Where(word => left ? word.BoundingBox.Right <= column : word.BoundingBox.Left >= column).ToArray())
                    .Where(line => line.Length > 0).Select(line => new TextLine(line, " ")).OrderByDescending(line => line.BoundingBox.Top).ToArray();
                if (lines.Length > 0) result.Add(new TextBlock(lines, "\n"));
            }
        }
        return result;
    }

    // A reading-order detector may alternate whole paragraphs between columns.
    // Only reorder when several lines on both sides share a clear vertical gutter.
    private static IReadOnlyList<TextBlock> OrderColumns(IReadOnlyList<TextBlock> blocks, double pageWidth)
    {
        if (blocks.Count < 2 || pageWidth <= 0) return blocks;
        var substantial = blocks.Where(b => b.TextLines.Count >= 2).ToArray();
        double? gutter = null, bestGap = pageWidth * .003;
        int bestCoverage = 0;
        for (int step = 100; step <= 400; step++)
        {
            double x = pageWidth * step / 500.0;
            var left = substantial.Where(b => b.BoundingBox.Right < x).ToArray();
            var right = substantial.Where(b => b.BoundingBox.Left > x).ToArray();
            if (left.Sum(b => b.TextLines.Count) < 4 || right.Sum(b => b.TextLines.Count) < 4) continue;
            double overlap = Math.Min(left.Max(b => b.BoundingBox.Top), right.Max(b => b.BoundingBox.Top)) -
                             Math.Max(left.Min(b => b.BoundingBox.Bottom), right.Min(b => b.BoundingBox.Bottom));
            if (overlap < pageWidth * .05) continue;
            double gap = right.Min(b => b.BoundingBox.Left) - left.Max(b => b.BoundingBox.Right);
            int coverage = left.Sum(b => b.TextLines.Count) + right.Sum(b => b.TextLines.Count);
            if (gap > pageWidth * .003 && (coverage > bestCoverage || (coverage == bestCoverage && gap > bestGap)))
            { bestCoverage = coverage; bestGap = gap; gutter = (right.Min(b => b.BoundingBox.Left) + left.Max(b => b.BoundingBox.Right)) / 2; }
        }
        if (gutter is not { } split) return blocks;

        bool Spans(TextBlock b) => b.BoundingBox.Left < split && b.BoundingBox.Right > split;
        double Middle(TextBlock b) => (b.BoundingBox.Top + b.BoundingBox.Bottom) / 2;
        var separators = blocks.Where(Spans).OrderByDescending(Middle).ToArray();
        return blocks.Select((block, index) => new
        {
            Block = block, Index = index,
            Band = Spans(block) ? Array.IndexOf(separators, block) * 2 + 1 : separators.Count(s => Middle(s) > Middle(block)) * 2,
            Side = block.BoundingBox.Right <= split ? 0 : 1
        }).OrderBy(x => x.Band).ThenBy(x => x.Side).ThenByDescending(x => x.Block.BoundingBox.Top)
          .ThenBy(x => x.Index).Select(x => x.Block).ToArray();
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
