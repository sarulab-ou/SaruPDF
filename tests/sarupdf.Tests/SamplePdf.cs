using System.Globalization;
using System.Text;

namespace SaruPdf.Tests;

internal static class SamplePdf
{
    // Deterministic, original test documents. No personal/user documents are read.
    public static void Write(string path, string name)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [4 0 R 6 0 R 8 0 R] /Count 3 >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
        };
        for (int page = 1; page <= 3; page++)
        {
            int streamId = 5 + (page - 1) * 2;
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 3 0 R >> >> /Contents {streamId} 0 R >>");
            var stream = $"0.14 0.25 0.45 rg 48 720 499 72 re f\nBT /F1 25 Tf 1 1 1 rg 65 745 Td ({name} - page {page}) Tj ET\nBT /F1 13 Tf 0.1 0.1 0.1 rg 55 670 Td (SaruPDF acceptance test / local PDF rendering) Tj 0 -32 Td (Use the mouse wheel to scroll between pages.) Tj 0 -32 Td (Reopening this document should preserve page and zoom.) Tj ET\n";
            objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}endstream");
        }
        WriteObjects(path, objects);
    }

    internal static void WriteJapanese(string path)
    {
        string text = Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes("日本語の本文です。コメントを追加します。"));
        string stream = $"BT /F1 16 Tf 1 0 0 1 72 762 Tm <{text}> Tj ET\n";
        WriteObjects(path,
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 7 0 R >>",
            "<< /Type /Font /Subtype /Type0 /BaseFont /Gothic /Encoding /UniJIS-UTF16-H /DescendantFonts [5 0 R] >>",
            "<< /Type /Font /Subtype /CIDFontType0 /BaseFont /Gothic /CIDSystemInfo << /Registry (Adobe) /Ordering (Japan1) /Supplement 6 >> /FontDescriptor 6 0 R >>",
            "<< /Type /FontDescriptor /FontName /Gothic /FontBBox [-200 -200 1200 1200] /Flags 4 /ItalicAngle 0 /Ascent 1000 /Descent -200 /StemV 80 >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}endstream"
        ]);
    }

    internal static void WriteTwoColumns(string path)
    {
        // Interleave the PDF drawing order deliberately; reading order should
        // follow columns and paragraphs rather than drawing order or rows.
        var stream = new StringBuilder("BT /F1 16 Tf 1 0 0 1 60 780 Tm (A wide heading across both columns) Tj ET\n");
        for (int line = 0; line < 8; line++)
        {
            int y = 740 - line * 17 - (line >= 4 ? 20 : 0);
            stream.Append($"BT /F1 12 Tf 1 0 0 1 60 {y} Tm (Left paragraph line {line + 1} with some text.) Tj ET\n");
            stream.Append($"BT /F1 12 Tf 1 0 0 1 330 {y} Tm (Right paragraph line {line + 1} with text.) Tj ET\n");
        }
        string content = stream.ToString();
        WriteObjects(path,
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 3 0 R >> >> /Contents 5 0 R >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream"
        ]);
    }

    internal static void WriteNarrowColumns(string path)
    {
        var stream = new StringBuilder("BT /F1 14 Tf 1 0 0 1 60 760 Tm (Full width heading) Tj ET\n");
        stream.Append("BT /F1 9 Tf 1 0 0 1 320 610 Tm (Figure caption on the right side) Tj ET\n");
        for (int i = 1; i <= 10; i++)
        {
            string left = $"Left{i:00} alpha beta gamma delta words here";
            string right = $"Right{i:00} alpha beta gamma delta text here";
            string Scale(string value) => (24900.0 / (value.Length * 6)).ToString("0.######", CultureInfo.InvariantCulture);
            int y = 400 - (i - 1) * 12;
            stream.Append($"BT /F1 10 Tf {Scale(left)} Tz 1 0 0 1 49 {y} Tm ({left}) Tj ET\n");
            stream.Append($"BT /F1 10 Tf {Scale(right)} Tz 1 0 0 1 302 {y + 1} Tm ({right}) Tj ET\n");
        }
        string content = stream.ToString();
        WriteObjects(path,
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents 5 0 R >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream"
        ]);
    }

    private static void WriteObjects(string path, List<string> objects)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var output = new FileStream(path, FileMode.Create, FileAccess.Write);
        void Put(string text) => output.Write(Encoding.ASCII.GetBytes(text));
        Put("%PDF-1.4\n");
        List<long> offsets = [0];
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(output.Position);
            Put($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        long xref = output.Position;
        Put($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (long offset in offsets.Skip(1)) Put(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        Put($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    }
}
