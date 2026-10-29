using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using SaruPdf.Documents;

namespace SaruPdf.Views;

internal sealed class PdfView : UserControl
{
    private const string RuntimeDownload = "https://developer.microsoft.com/microsoft-edge/webview2/#download-section";
    private readonly PdfDocument document;
    private readonly WebView2 web = new() { Dock = DockStyle.Fill, AllowExternalDrop = false, DefaultBackgroundColor = Color.White };
    private readonly Label notice = new() { Dock = DockStyle.Fill, Text = "PDF Viewer を準備しています…", TextAlign = ContentAlignment.MiddleCenter, Padding = new Padding(24), AutoEllipsis = true };
    private readonly Button runtimeLink = new() { Dock = DockStyle.Bottom, Height = 44, Text = "Microsoft 公式サイトで WebView2 Runtime を入手", Visible = false };
    private bool started;
    public event Action<string>? OpenRequested;
    public event Action? OpenDialogRequested;
    public event Action? CloseRequested;

    public PdfView(PdfDocument document)
    {
        this.document = document;
        Controls.Add(web);
        Controls.Add(notice);
        Controls.Add(runtimeLink);
        notice.BringToFront();
        runtimeLink.Click += (_, _) => OpenBrowser(new Uri(RuntimeDownload));
    }

    public async Task LoadAsync()
    {
        if (started || IsDisposed) return;
        started = true;
        try
        {
            string userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SaruPDF", "WebView2", $"Session-{Process.GetCurrentProcess().SessionId}");
            var environment = await CoreWebView2Environment.CreateAsync(null, userData);
            if (IsDisposed || Disposing) return;
            await web.EnsureCoreWebView2Async(environment);
            if (IsDisposed || Disposing) return;
            var core = web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsWebMessageEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.HiddenPdfToolbarItems = Enum.GetValues<CoreWebView2PdfToolbarItems>()
                .Aggregate(CoreWebView2PdfToolbarItems.None, (all, item) => all | item);
            core.NavigationStarting += NavigationStarting;
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (e.IsUserInitiated) RouteLink(e.Uri);
            };
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.LaunchingExternalUriScheme += (_, e) => e.Cancel = true;
            core.NavigationCompleted += (_, e) =>
            {
                if (IsDisposed) return;
                if (e.IsSuccess) { notice.Visible = false; runtimeLink.Visible = false; }
                else ShowFailure($"PDF を表示できませんでした（{e.WebErrorStatus}）。\nファイルが破損していないか、アクセス可能か確認してください。", false);
            };
            core.ProcessFailed += (_, e) => ShowFailure($"PDF Viewer が停止しました（{e.ProcessFailedKind}）。\nこのウィンドウを閉じて、PDF を開き直してください。", false);
            web.KeyDown += (_, e) =>
            {
                if (e.Modifiers != Keys.Control || e.KeyCode is not (Keys.O or Keys.W)) return;
                e.Handled = true;
                e.SuppressKeyPress = true;
                bool open = e.KeyCode == Keys.O;
                // A synchronous dialog inside this callback would block WebView2.
                BeginInvoke(() =>
                {
                    if (IsDisposed) return;
                    if (open) OpenDialogRequested?.Invoke();
                    else CloseRequested?.Invoke();
                });
            };
            var uri = new UriBuilder(document.Uri) { Fragment = "toolbar=0" }.Uri;
            core.Navigate(uri.AbsoluteUri);
        }
        catch (Exception ex) when (IsDisposed || Disposing) { _ = ex; }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowFailure("WebView2 Runtime が見つかりません。\n下の Microsoft 公式サイトから Evergreen Runtime（x64）をインストールし、\nSaruPDF のすべてのウィンドウを閉じてから再起動してください。", true);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException or DllNotFoundException)
        {
            ShowFailure($"PDF Viewer を初期化できませんでした。\n{ex.Message}\nWebView2 Runtime とユーザーデータフォルダーのアクセス権を確認してください。", true);
        }
    }

    private void NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && DocumentPath.IsSameDocument(uri, document)) return;
        e.Cancel = true;
        if (e.IsUserInitiated) RouteLink(e.Uri);
    }

    private void RouteLink(string target)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri)) return;
        if (uri.IsFile && string.Equals(Path.GetExtension(uri.LocalPath), ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            BeginInvoke(() => { if (!IsDisposed) OpenRequested?.Invoke(uri.LocalPath); });
        }
        else if (uri.Scheme is "https" or "http") OpenBrowser(uri);
    }

    private void OpenBrowser(Uri uri)
    {
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        { ShowFailure($"既定のブラウザーを開けませんでした。\n{ex.Message}", false); }
    }

    private void ShowFailure(string text, bool showRuntimeLink)
    {
        if (IsDisposed || Disposing) return;
        notice.Text = text;
        notice.Visible = true;
        notice.BringToFront();
        runtimeLink.Visible = showRuntimeLink;
        runtimeLink.BringToFront();
    }
}
