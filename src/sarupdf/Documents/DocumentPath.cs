namespace SaruPdf.Documents;

internal sealed record PdfDocument(string Path, Uri Uri)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}

internal static class DocumentPath
{
    public static string Normalize(string path, string workingDirectory) =>
        Path.GetFullPath(path, workingDirectory);

    public static PdfDocument Open(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(fullPath), ".pdf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("PDF ファイル（.pdf）を選択してください。");

        // Actually open the file: File.Exists alone hides access-denied errors.
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        Span<byte> header = stackalloc byte[1024];
        int length = stream.Read(header);
        if (header[..length].IndexOf("%PDF-"u8) < 0)
            throw new InvalidDataException("PDF のヘッダーを確認できません。ファイルの形式を確認してください。");

        return new PdfDocument(fullPath, new Uri(fullPath, UriKind.Absolute));
    }

    public static bool IsSameDocument(Uri uri, PdfDocument document)
    {
        if (!uri.IsFile) return false;
        try
        {
            return string.Equals(Path.GetFullPath(uri.LocalPath), document.Path, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        { return false; }
    }

    public static string Explain(Exception error) => error switch
    {
        FileNotFoundException or DirectoryNotFoundException => "ファイルが見つかりません。移動・削除されていないか確認してください。",
        UnauthorizedAccessException => "ファイルを読み取る権限がありません。アクセス権を確認してください。",
        InvalidDataException => error.Message,
        IOException => "ファイルを読み取れません。他のアプリによるロックや保存先を確認してください。",
        ArgumentException or NotSupportedException => "ファイルパスが正しくありません。",
        _ => error.Message
    };
}
