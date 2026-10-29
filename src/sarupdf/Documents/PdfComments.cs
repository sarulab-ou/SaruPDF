using System.Globalization;
using System.Text;
using PdfSharp.Pdf;
using SharpDocument = PdfSharp.Pdf.PdfDocument;
using PdfSharp.Pdf.Annotations;

namespace SaruPdf.Documents;

internal readonly record struct PdfPointValue(double X, double Y);
// Vertices in counterclockwise order in unrotated default PDF user space.
internal sealed record PdfCommentQuad(PdfPointValue BottomLeft, PdfPointValue BottomRight, PdfPointValue TopRight, PdfPointValue TopLeft)
{
    internal IEnumerable<PdfPointValue> Points => [BottomLeft, BottomRight, TopRight, TopLeft];
}
internal sealed record PdfCommentRegion(int Page, IReadOnlyList<PdfCommentQuad> Quads);
internal sealed record PdfCommentMark(Guid Id, int PageId, string Text, IReadOnlyList<PdfCommentQuad> Quads, DateTime Created);

internal static class PdfComments
{
    private sealed class StoredAnnotation(SharpDocument document) : PdfAnnotation(document);

    internal static void Write(SharpDocument document, PdfPage page, PdfCommentMark mark)
    {
        var points = mark.Quads.SelectMany(quad => quad.Points).ToArray();
        double left = points.Min(p => p.X), bottom = points.Min(p => p.Y), right = points.Max(p => p.X), top = points.Max(p => p.Y);
        var annotation = new StoredAnnotation(document);
        annotation.Elements.SetName("/Subtype", "/Highlight");
        annotation.Elements.SetRectangle("/Rect", new PdfRectangle(new PdfSharp.Drawing.XPoint(left, bottom), new PdfSharp.Drawing.XPoint(right, top)));
        annotation.Elements["/Contents"] = new PdfString(mark.Text.ReplaceLineEndings("\r"), PdfStringEncoding.Unicode);
        annotation.Elements.SetString("/T", "SaruPDF");
        annotation.Elements.SetString("/NM", mark.Id.ToString("D"));
        annotation.Elements.SetDateTime("/M", mark.Created);
        annotation.Elements.SetDateTime("/CreationDate", mark.Created);
        annotation.Elements.SetInteger("/F", 4); // Print; comments remain editable annotations.
        annotation.Elements.SetReal("/CA", .35);
        annotation.Elements["/C"] = Numbers(document, [1, .85, 0]);
        annotation.Elements["/QuadPoints"] = Numbers(document, points.SelectMany(p => new[] { p.X, p.Y }));
        page.Annotations.Add(annotation);
        annotation.Elements["/P"] = page.Reference!;

        // Supply an explicit normal appearance so viewers need not synthesize it.
        var appearance = new PdfDictionary(document);
        appearance.Elements.SetName("/Type", "/XObject"); appearance.Elements.SetName("/Subtype", "/Form");
        appearance.Elements.SetInteger("/FormType", 1);
        appearance.Elements["/BBox"] = Numbers(document, [left, bottom, right, top]);
        var state = new PdfDictionary(document);
        state.Elements.SetName("/Type", "/ExtGState"); state.Elements.SetReal("/ca", .35); state.Elements.SetName("/BM", "/Multiply");
        var states = new PdfDictionary(document); states.Elements["/GS0"] = state;
        var resources = new PdfDictionary(document); resources.Elements["/ExtGState"] = states;
        appearance.Elements["/Resources"] = resources;
        var commands = new StringBuilder("q /GS0 gs 1 0.85 0 rg\n");
        static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
        foreach (var quad in mark.Quads)
        {
            int index = 0;
            foreach (var point in quad.Points) commands.Append($"{Number(point.X)} {Number(point.Y)} {(index++ == 0 ? "m" : "l")} ");
            commands.Append("h f\n");
        }
        commands.Append("Q\n"); appearance.CreateStream(Encoding.ASCII.GetBytes(commands.ToString()));
        document.Internals.AddObject(appearance);
        var appearances = new PdfDictionary(document); appearances.Elements["/N"] = appearance.Reference!;
        annotation.Elements["/AP"] = appearances;

        var popup = new StoredAnnotation(document);
        popup.Elements.SetName("/Subtype", "/Popup");
        popup.Elements.SetRectangle("/Rect", new PdfRectangle(new PdfSharp.Drawing.XPoint(right + 8, bottom), new PdfSharp.Drawing.XPoint(right + 248, bottom + 140)));
        popup.Elements.SetBoolean("/Open", false);
        popup.Elements["/Parent"] = annotation.Reference!;
        page.Annotations.Add(popup); popup.Elements["/P"] = page.Reference!;
        annotation.Elements["/Popup"] = popup.Reference!;
        WriteLabel(document, page, annotation, mark);
    }

    private static void WriteLabel(SharpDocument document, PdfPage page, PdfAnnotation parent, PdfCommentMark mark)
    {
        var geometry = PdfTextPage.FromPdfPage(page);
        using var label = new PdfCommentLabel(geometry, mark.Quads, mark.Text);
        var points = label.PdfBounds().Points.ToArray();
        double left = points.Min(p => p.X), bottom = points.Min(p => p.Y), right = points.Max(p => p.X), top = points.Max(p => p.Y);
        var annotation = new StoredAnnotation(document);
        annotation.Elements.SetName("/Subtype", "/FreeText");
        annotation.Elements.SetRectangle("/Rect", new PdfRectangle(new PdfSharp.Drawing.XPoint(left, bottom), new PdfSharp.Drawing.XPoint(right, top)));
        annotation.Elements["/Contents"] = new PdfString(mark.Text.ReplaceLineEndings("\r"), PdfStringEncoding.Unicode);
        annotation.Elements.SetString("/DA", "1 0 0 rg /Helv 11 Tf");
        annotation.Elements.SetString("/T", "SaruPDF");
        annotation.Elements.SetString("/NM", mark.Id.ToString("D") + "-label");
        annotation.Elements.SetDateTime("/M", mark.Created); annotation.Elements.SetDateTime("/CreationDate", mark.Created);
        annotation.Elements.SetInteger("/F", 4); annotation.Elements.SetInteger("/Q", 0);
        annotation.Elements["/Border"] = Numbers(document, [0, 0, 0]);
        annotation.Elements["/C"] = Numbers(document, []); // Transparent background; red belongs to DA/AP.
        annotation.Elements["/IRT"] = parent.Reference!;
        annotation.Elements.SetName("/RT", "/Group");
        page.Annotations.Add(annotation); annotation.Elements["/P"] = page.Reference!;

        var appearance = new PdfDictionary(document);
        appearance.Elements.SetName("/Type", "/XObject"); appearance.Elements.SetName("/Subtype", "/Form");
        appearance.Elements.SetInteger("/FormType", 1);
        appearance.Elements["/BBox"] = Numbers(document, [left, bottom, right, top]);
        var font = new PdfDictionary(document);
        font.Elements.SetName("/Type", "/Font"); font.Elements.SetName("/Subtype", "/Type1"); font.Elements.SetName("/BaseFont", "/Helvetica");
        var fonts = new PdfDictionary(document); fonts.Elements["/Helv"] = font;
        var resources = new PdfDictionary(document); resources.Elements["/Font"] = fonts;
        appearance.Elements["/Resources"] = resources;
        var commands = new StringBuilder("q 1 0 0 rg\n");
        var path = label.Outline.PointCount == 0 ? [] : label.Outline.PathPoints;
        var types = label.Outline.PointCount == 0 ? [] : label.Outline.PathTypes;
        string Point(int index)
        {
            var value = geometry.ToPdf(label.Bounds.Left + path[index].X, geometry.Height - label.Bounds.Top - path[index].Y);
            return value.X.ToString("0.######", CultureInfo.InvariantCulture) + " " + value.Y.ToString("0.######", CultureInfo.InvariantCulture);
        }
        for (int i = 0; i < path.Length; i++)
        {
            int type = types[i] & 7;
            if (type == 0) commands.Append(Point(i)).Append(" m\n");
            else if (type == 1) commands.Append(Point(i)).Append(" l\n");
            else if (type == 3 && i + 2 < path.Length)
            {
                commands.Append(Point(i)).Append(' ').Append(Point(i + 1)).Append(' ').Append(Point(i + 2)).Append(" c\n"); i += 2;
            }
            if ((types[i] & 128) != 0) commands.Append("h\n");
        }
        commands.Append("f* Q\n"); appearance.CreateStream(Encoding.ASCII.GetBytes(commands.ToString()));
        document.Internals.AddObject(appearance);
        var appearances = new PdfDictionary(document); appearances.Elements["/N"] = appearance.Reference!;
        annotation.Elements["/AP"] = appearances;
    }

    private static PdfArray Numbers(SharpDocument document, IEnumerable<double> values)
    {
        var result = new PdfArray(document);
        foreach (double value in values) result.Elements.Add(new PdfReal(value));
        return result;
    }
}
