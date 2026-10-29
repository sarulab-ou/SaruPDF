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
            var stream = $"0.14 0.25 0.45 rg 48 720 499 72 re f\nBT /F1 25 Tf 1 1 1 rg 65 745 Td ({name} - page {page}) Tj ET\nBT /F1 13 Tf 0.1 0.1 0.1 rg 55 670 Td (SaruPDF acceptance test / local PDF rendering) Tj 0 -32 Td (Use page navigation, zoom and search in the standard toolbar.) Tj 0 -32 Td (Reopening this document should preserve page and zoom.) Tj ET\n";
            objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}endstream");
        }
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
