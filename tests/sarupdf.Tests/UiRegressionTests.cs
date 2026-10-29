using SaruPdf.Documents;
using SaruPdf.Views;

namespace SaruPdf.Tests;

internal static class UiRegressionTests
{
    internal static IEnumerable<(string Name, Action Run)> Cases(string pdfPath)
    {
        yield return ("association close button exits a standalone application window", () => OnSta(() => TestClose(false)));
        yield return ("association close button closes a modal settings dialog", () => OnSta(() => TestClose(true)));
        yield return ("PDF title includes filename and full path without the app name", () => OnSta(() =>
        {
            using var window = new PdfWindow();
            window.SetDocument(DocumentPath.Open(pdfPath));
            Check(window.Text == $"{Path.GetFileName(pdfPath)}（{pdfPath}）");
        }));
        yield return ("embedded PDF icon contains pure red text with transparency", () =>
        {
            using var icon = AppIcon.Create();
            using var bitmap = icon.ToBitmap();
            int red = 0, transparent = 0;
            for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.A == 0) transparent++;
                if (pixel.A == 255 && pixel.R == 255 && pixel.G == 0 && pixel.B == 0) red++;
            }
            Check(red > 10 && transparent > 10);
        });
    }

    private static void TestClose(bool modal)
    {
        using var dialog = new PdfAssociationDialog();
        using var timeout = new System.Windows.Forms.Timer { Interval = 2500 };
        bool timedOut = false;
        bool closed = false;
        timeout.Tick += (_, _) => { timedOut = true; timeout.Stop(); dialog.Close(); };
        dialog.FormClosed += (_, _) => closed = true;
        dialog.Shown += (_, _) => dialog.BeginInvoke(() =>
        {
            var button = dialog.Controls.Find("CloseButton", true).OfType<Button>().Single();
            button.PerformClick();
        });
        timeout.Start();
        if (modal) Check(dialog.ShowDialog() == DialogResult.Cancel);
        else Application.Run(dialog);
        Check(closed && !timedOut);
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(10))) throw new Exception("UI test thread timed out.");
        if (failure is not null) throw new Exception("UI regression failed.", failure);
    }

    private static void Check(bool condition) { if (!condition) throw new Exception("UI assertion failed."); }
}
