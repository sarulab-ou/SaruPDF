namespace SaruPdf.Views;

internal static class AppIcon
{
    internal static Icon Create()
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("SaruPdf.Assets.App.ico")
            ?? throw new InvalidOperationException("PDF アイコンが見つかりません。");
        using var icon = new Icon(stream, 32, 32);
        return (Icon)icon.Clone();
    }

    internal static void Apply(Form form)
    {
        var icon = Create();
        form.Icon = icon;
        form.Disposed += (_, _) => icon.Dispose();
    }
}
