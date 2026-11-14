using System.Text.Json;
using SaruPdf.Views;

namespace SaruPdf.Updates;

internal enum PdfDisplayMode { View, Pages, Comments }
internal sealed record WindowPlacement(int X, int Y, int Width, int Height, bool Maximized);
internal sealed record PdfWindowState(string? Path, PdfDisplayMode Mode, PdfPageViewState? View, WindowPlacement? Placement = null, int? Caret = null, int? Anchor = null);
internal sealed record UpdateSession(int Format, PdfWindowState[] Windows, int ActiveIndex);

internal static class UpdateSessionFile
{
    internal const string Switch = "--restore-session";
    internal static void Write(string path, UpdateSession session)
    {
        Validate(session);
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(session);
        if (data.Length > 1024 * 1024) throw new InvalidDataException("更新前の表示状態が大きすぎます。");
        File.WriteAllBytes(path, data);
    }
    internal static UpdateSession Read(string path)
    {
        using var input = File.OpenRead(path);
        if (input.Length > 1024 * 1024) throw new InvalidDataException("復元ファイルが大きすぎます。");
        var session = JsonSerializer.Deserialize<UpdateSession>(input, new JsonSerializerOptions { MaxDepth = 16 }) ?? throw new InvalidDataException("復元ファイルが空です。");
        Validate(session); return session;
    }
    private static void Validate(UpdateSession session)
    {
        if (session.Format != 1 || session.Windows is not { Length: > 0 and <= 256 } || session.ActiveIndex < 0 || session.ActiveIndex >= session.Windows.Length)
            throw new InvalidDataException("表示状態の形式を確認できません。");
        foreach (var window in session.Windows)
        {
            if (window is null || !Enum.IsDefined(window.Mode)) throw new InvalidDataException("表示モードが不正です。");
            if (window.Caret < 0 || window.Anchor < 0) throw new InvalidDataException("キャレット位置が不正です。");
            if (window.Path is not null && (!Path.IsPathFullyQualified(window.Path) || !Path.GetExtension(window.Path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("復元するPDFのパスが不正です。");
            if (window.View is { } view && (view.Page < 0 || view.Page > 10000000 || !double.IsFinite(view.Width) || view.Width <= 0 || view.Width > 100000 ||
                !double.IsFinite(view.CenterX) || !double.IsFinite(view.CenterY) || Math.Abs(view.CenterX) > 1000 || Math.Abs(view.CenterY) > 1000))
                throw new InvalidDataException("PDFの表示位置が不正です。");
            if (window.Placement is { } p && (p.Width < 1 || p.Height < 1 || p.Width > 32768 || p.Height > 32768 || Math.Abs((long)p.X) > 1000000 || Math.Abs((long)p.Y) > 1000000))
                throw new InvalidDataException("ウィンドウの位置が不正です。");
        }
    }
}
