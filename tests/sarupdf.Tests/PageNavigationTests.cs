using System.Drawing.Imaging;
using System.Reflection;
using System.Text;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using SaruPdf.Documents;
using SaruPdf.Views;

namespace SaruPdf.Tests;

internal static class PageNavigationTests
{
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("page number input accepts multiple digits", () =>
        {
            var input = new PageNumberInput();
            input.Append(1); input.Append(2); input.Append(3);
            Check(input.Text == "123" && input.TryGetPage(out int page) && page == 123);
        });
        yield return ("page number input supports correction and cancellation", () =>
        {
            var input = new PageNumberInput();
            input.Append(1); input.Append(2); input.Backspace();
            Check(input.Text == "1");
            input.Backspace(); input.Backspace();
            Check(!input.IsActive);
            input.Append(3); input.Clear();
            Check(!input.IsActive && !input.TryGetPage(out _));
        });
        yield return ("empty and zero page numbers do not navigate", () =>
        {
            var input = new PageNumberInput();
            Check(!input.TryGetPage(out _));
            input.Append(0);
            Check(!input.TryGetPage(out _));
            input.Append(2);
            Check(input.TryGetPage(out int page) && page == 2);
        });
        yield return ("page number input bounds length and rejects integer overflow", () =>
        {
            var input = new PageNumberInput();
            foreach (char digit in "2147483648") Check(input.Append(digit - '0'));
            Check(!input.TryGetPage(out _));
            Check(!input.Append(1) && input.Text == "2147483648");
        });
    }

    internal static Task VerifyRenderingAsync(string directory)
    {
        string pdf = Path.Combine(directory, "page-colors.pdf");
        WriteColorPdf(pdf);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            using var host = new RenderTestWindow();
            using var viewer = new PdfView(DocumentPath.Open(pdf), Path.Combine(directory, "render-profile-" + Guid.NewGuid().ToString("N"))) { Dock = DockStyle.Fill };
            host.Controls.Add(viewer);
            host.Shown += async (_, _) =>
            {
                try
                {
                    var web = (WebView2)typeof(PdfView).GetField("web", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewer)!;
                    var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    web.NavigationCompleted += (_, e) =>
                    {
                        if (e.IsSuccess && web.Source?.IsFile == true) loaded.TrySetResult();
                    };
                    await viewer.LoadAsync();
                    if (web.CoreWebView2 is null)
                        throw new Exception("PDF test initialization failed: " + string.Join(" / ", viewer.Controls.OfType<Label>().Select(label => label.Text)));
                    await loaded.Task.WaitAsync(TimeSpan.FromSeconds(12));
                    await ExpectColorAsync(web, Color.Red);
                    Check(viewer.TryHandlePageKey(Keys.D2));
                    Check(viewer.TryHandlePageKey(Keys.Enter));
                    await ExpectColorAsync(web, Color.Lime);
                    Check(viewer.TryHandlePageKey(Keys.NumPad3));
                    Check(viewer.TryHandlePageKey(Keys.Enter));
                    await ExpectColorAsync(web, Color.Blue);
                    Check(viewer.TryHandlePageKey(Keys.D1));
                    Check(viewer.TryHandlePageKey(Keys.D2));
                    Check(viewer.TryHandlePageKey(Keys.Enter));
                    await ExpectColorAsync(web, Color.Yellow);
                    Check(viewer.TryHandlePageKey(Keys.D1));
                    Check(viewer.TryHandlePageKey(Keys.Enter));
                    await ExpectColorAsync(web, Color.Red);
                    Check(viewer.TryHandlePageKey(Keys.D3));
                    Check(viewer.TryHandlePageKey(Keys.Escape));
                    Check(!viewer.TryHandlePageKey(Keys.Enter));
                    await ExpectColorAsync(web, Color.Red);
                    Check(viewer.TryHandlePageKey(Keys.D0));
                    Check(viewer.TryHandlePageKey(Keys.Enter));
                    await ExpectColorAsync(web, Color.Red);
                    Check(viewer.TryHandlePageKey(Keys.Back));
                    Check(!viewer.TryHandlePageKey(Keys.Enter));
                    Check(!viewer.TryHandlePageKey(Keys.Control | Keys.D2));
                    done.TrySetResult();
                }
                catch (Exception ex) { done.TrySetException(ex); }
                finally { host.Close(); }
            };
            Application.Run(host);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }

    private static async Task ExpectColorAsync(WebView2 web, Color expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(12);
        Color actual = Color.Empty;
        while (DateTime.UtcNow < deadline)
        {
            using var image = new MemoryStream();
            try { await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image); }
            catch (InvalidOperationException) when (!web.IsDisposed)
            {
                await Task.Delay(150);
                continue;
            }
            image.Position = 0;
            using var bitmap = new Bitmap(image);
            actual = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);
            if (Math.Abs(actual.R - expected.R) < 12 && Math.Abs(actual.G - expected.G) < 12 && Math.Abs(actual.B - expected.B) < 12) return;
            await Task.Delay(150);
        }
        throw new Exception($"PDF page did not change: expected {expected}, rendered {actual}, source {web.Source}.");
    }

    // Original twelve-page PDF: distinct colors make the rendered-page
    // assertion independent of metadata, URI fragments and accessibility text.
    private static void WriteColorPdf(string path)
    {
        string[] colors = ["1 0 0", "0 1 0", "0 0 1", "0.5 0 0", "0 0.5 0", "0 0 0.5", "1 0.5 0", "0 0.5 1", "0.5 0 1", "0.5 1 0", "1 0 0.5", "1 1 0"];
        string kids = string.Join(" ", Enumerable.Range(0, colors.Length).Select(i => $"{3 + i * 2} 0 R"));
        List<string> objects = ["<< /Type /Catalog /Pages 2 0 R >>", $"<< /Type /Pages /Kids [{kids}] /Count {colors.Length} >>"];
        for (int i = 0; i < colors.Length; i++)
        {
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << >> /Contents {4 + i * 2} 0 R >>");
            string stream = $"{colors[i]} rg 0 0 595 842 re f\n";
            objects.Add($"<< /Length {stream.Length} >>\nstream\n{stream}endstream");
        }
        using var file = File.Create(path);
        void Write(string value) => file.Write(Encoding.ASCII.GetBytes(value));
        Write("%PDF-1.4\n");
        List<long> offsets = [];
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(file.Position);
            Write($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        long xref = file.Position;
        Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (long offset in offsets) Write($"{offset:D10} 00000 n \n");
        Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    }

    private sealed class RenderTestWindow : Form
    {
        internal RenderTestWindow()
        {
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-20000, -20000);
            ClientSize = new Size(800, 600);
        }
        protected override bool ShowWithoutActivation => true;
    }

    private static void Check(bool condition) { if (!condition) throw new Exception("Page navigation assertion failed."); }
}
