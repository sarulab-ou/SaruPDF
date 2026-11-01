using System.Text;

namespace SaruPdf.Documents;

internal sealed record PdfSearchMatch(int Page, IReadOnlyList<RectangleF> Bounds);

// Keep a UTF-16 offset-to-glyph map so Japanese, surrogate pairs and matches
// spanning several extracted glyphs retain their actual page rectangles.
internal static class PdfSearch
{
    internal static IReadOnlyList<PdfSearchMatch> Find(PdfTextMap map, string term)
    {
        if (string.IsNullOrEmpty(term)) return [];
        List<PdfSearchMatch> results = [];
        foreach (var page in map.Glyphs.GroupBy(glyph => glyph.Page))
        {
            var text = new StringBuilder();
            List<PdfTextGlyph> positions = [];
            foreach (var glyph in page)
            {
                text.Append(glyph.Text);
                for (int i = 0; i < glyph.Text.Length; i++) positions.Add(glyph);
            }
            string content = text.ToString();
            int offset = 0;
            while (offset <= content.Length - term.Length)
            {
                int index = content.IndexOf(term, offset, StringComparison.OrdinalIgnoreCase);
                if (index < 0) break;
                var bounds = positions.Skip(index).Take(term.Length).Where(g => !string.IsNullOrWhiteSpace(g.Text))
                    .GroupBy(g => g.Line).Select(line => line.Select(g => g.Bounds).Aggregate(RectangleF.Union)).ToArray();
                if (bounds.Length > 0) results.Add(new(page.Key, bounds));
                offset = index + Math.Max(1, term.Length);
            }
        }
        return results;
    }
}
