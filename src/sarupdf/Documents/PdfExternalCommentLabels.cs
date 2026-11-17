using System.Globalization;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace SaruPdf.Documents;

internal sealed record PdfExternalCommentLabel(int Page, string Text, PdfCommentLabel Label) : IDisposable
{
    public void Dispose() => Label.Dispose();
}

// Display-only labels: never import other authors' annotations into managed
// editing state or change their Contents, appearances, author or identifiers.
internal static class PdfExternalCommentLabels
{
    internal static List<PdfExternalCommentLabel> Read(string path)
    {
        List<PdfExternalCommentLabel> result = [];
        try
        {
            using var input = new MemoryStream(File.ReadAllBytes(path), false);
            using var pdf = PdfReader.Open(input, PdfDocumentOpenMode.Import);
            var marks = new List<(int Page, string Text, PdfTextPage Geometry, List<PdfCommentQuad> Quads)>();
            for (int p = 0; p < pdf.PageCount; p++)
            foreach (PdfSharp.Pdf.Annotations.PdfAnnotation annotation in pdf.Pages[p].Annotations)
            {
                if (annotation.Elements.GetName("/Subtype") != "/Highlight" ||
                    (annotation.Elements.GetInteger("/F") & (1 | 2 | 32)) != 0 ||
                    (annotation.Elements.GetString("/T") == "SaruPDF" && Guid.TryParse(annotation.Elements.GetString("/NM"), out _))) continue;
                string text = annotation.Elements.GetString("/Contents");
                if (string.IsNullOrWhiteSpace(text)) continue;
                var quads = new List<PdfCommentQuad>();
                if (annotation.Elements.GetArray("/QuadPoints") is { } values)
                for (int q = 0; q + 7 < values.Elements.Count; q += 8)
                {
                    static double Number(PdfItem item) => item switch { PdfReal n => n.Value, PdfInteger n => n.Value, _ => double.NaN };
                    PdfPointValue Point(int i) => new(Number(values.Elements[q + i * 2]), Number(values.Elements[q + i * 2 + 1]));
                    var quad = new PdfCommentQuad(Point(0), Point(1), Point(2), Point(3));
                    if (quad.Points.All(point => double.IsFinite(point.X) && double.IsFinite(point.Y))) quads.Add(quad);
                }
                if (quads.Count > 0) marks.Add((p, text, PdfTextPage.FromPdfPage(pdf.Pages[p]), quads));
            }
            if (marks.Count == 0) return result;
            PdfTextMap? map;
            try { map = PdfTextMap.Read(path); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { map = null; }
            foreach (var mark in marks)
            {
                var label = new PdfCommentLabel(mark.Geometry, mark.Quads, mark.Text, map?.FindParagraph(mark.Page, mark.Quads[0]));
                if (label.Outline.PointCount == 0) { label.Dispose(); continue; }
                result.Add(new(mark.Page, mark.Text, label));
            }
            return result;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            foreach (var label in result) label.Dispose();
            return [];
        }
    }

    internal sealed record SvgLabel(int Page, double Width, double Height, float X, float Y, string Path);
    internal static SvgLabel[] ReadSvg(string path)
    {
        var labels = Read(path);
        try
        {
            return labels.Select(item =>
            {
                var label = item.Label; var points = label.Outline.PathPoints; var types = label.Outline.PathTypes;
                var data = new StringBuilder();
                string Point(int i) => points[i].X.ToString("0.######", CultureInfo.InvariantCulture) + " " + points[i].Y.ToString("0.######", CultureInfo.InvariantCulture);
                for (int i = 0; i < points.Length; i++)
                {
                    int kind = types[i] & 7;
                    if (kind == 0) data.Append('M').Append(Point(i));
                    else if (kind == 1) data.Append('L').Append(Point(i));
                    else if (kind == 3 && i + 2 < points.Length)
                    { data.Append('C').Append(Point(i)).Append(' ').Append(Point(i + 1)).Append(' ').Append(Point(i + 2)); i += 2; }
                    if ((types[i] & 128) != 0) data.Append('Z');
                }
                return new SvgLabel(item.Page, label.Page.Width, label.Page.Height, label.Bounds.Left, label.Bounds.Top, data.ToString());
            }).ToArray();
        }
        finally { foreach (var label in labels) label.Dispose(); }
    }
}
