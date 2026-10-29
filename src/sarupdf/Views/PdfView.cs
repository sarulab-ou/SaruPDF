using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using SaruPdf.Documents;

namespace SaruPdf.Views;

internal sealed class PdfView : UserControl
{
    private const string RuntimeDownload = "https://developer.microsoft.com/microsoft-edge/webview2/#download-section";
    private PdfDocument document;
    private int initialPage = 1;
    private readonly string? userDataFolder;
    private readonly WebView2 web = new() { Dock = DockStyle.Fill, AllowExternalDrop = false, DefaultBackgroundColor = Color.White };
    private readonly Label notice = new() { Dock = DockStyle.Fill, Text = "PDF Viewer を準備しています…", TextAlign = ContentAlignment.MiddleCenter, Padding = new Padding(24), AutoEllipsis = true };
    private readonly Button runtimeLink = new() { Dock = DockStyle.Bottom, Height = 44, Text = "Microsoft 公式サイトで WebView2 Runtime を入手", Visible = false };
    private readonly PageNumberInput pageInput = new();
    private readonly Label pageHint = new()
    {
        AutoSize = true, Visible = false, Padding = new Padding(12, 8, 12, 8),
        BackColor = Color.FromArgb(36, 48, 68), ForeColor = Color.White,
        AccessibleName = "移動先のページ番号", Anchor = AnchorStyles.Top | AnchorStyles.Right
    };
    private bool started;
    private bool ready;
    public event Action<string>? OpenRequested;
    public event Action? OpenDialogRequested;
    public event Action? CloseRequested;
    public event Func<Keys, bool>? CommandKeyRequested;
    public event Action<int>? PageJumpRequested;

    public PdfView(PdfDocument document, string? userDataFolder = null)
    {
        this.document = document;
        this.userDataFolder = userDataFolder;
        Controls.Add(web);
        Controls.Add(notice);
        Controls.Add(runtimeLink);
        Controls.Add(pageHint);
        notice.BringToFront();
        Resize += (_, _) => PositionPageHint();
        runtimeLink.Click += (_, _) => OpenBrowser(new Uri(RuntimeDownload));
    }

    public async Task LoadAsync()
    {
        if (started || IsDisposed) return;
        started = true;
        try
        {
            string userData = userDataFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SaruPDF", "WebView2", $"Session-{Process.GetCurrentProcess().SessionId}");
            var environment = await CoreWebView2Environment.CreateAsync(null, userData);
            if (IsDisposed || Disposing) return;
            var options = environment.CreateCoreWebView2ControllerOptions();
            options.AllowHostInputProcessing = true;
            await web.EnsureCoreWebView2Async(environment, options);
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
                ready = e.IsSuccess;
                if (e.IsSuccess) { notice.Visible = false; runtimeLink.Visible = false; }
                else ShowFailure($"PDF を表示できませんでした（{e.WebErrorStatus}）。\nファイルが破損していないか、アクセス可能か確認してください。", false);
            };
            core.ProcessFailed += (_, e) => ShowFailure($"PDF Viewer が停止しました（{e.ProcessFailedKind}）。\nこのウィンドウを閉じて、PDF を開き直してください。", false);
            web.KeyDown += (_, e) =>
            {
                if (CommandKeyRequested?.Invoke(e.KeyData) == true)
                {
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    return;
                }
                if (TryHandlePageKey(e.KeyData))
                {
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    return;
                }
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
            var uri = new UriBuilder(document.Uri) { Fragment = $"toolbar=0&page={initialPage}" }.Uri;
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

    internal bool TryHandlePageKey(Keys keyData)
    {
        if (!ready || (keyData & Keys.Modifiers) != Keys.None) return false;
        var key = keyData & Keys.KeyCode;
        int digit = key is >= Keys.D0 and <= Keys.D9 ? key - Keys.D0
            : key is >= Keys.NumPad0 and <= Keys.NumPad9 ? key - Keys.NumPad0 : -1;
        if (digit >= 0)
        {
            bool appended = pageInput.Append(digit);
            UpdatePageHint(appended ? null : "ページ番号は10桁以内で入力してください。");
            return true;
        }
        if (!pageInput.IsActive) return false;
        if (key == Keys.Back)
        {
            pageInput.Backspace();
            UpdatePageHint();
            return true;
        }
        if (key == Keys.Escape)
        {
            CancelPageInput();
            return true;
        }
        if (key == Keys.Enter)
        {
            if (!pageInput.TryGetPage(out int page)) UpdatePageHint("1以上のページ番号を入力してください。");
            else
            {
                CancelPageInput();
                // Queue navigation outside WebView2's synchronous keyboard callback.
                BeginInvoke(() => GoToPage(page));
            }
            return true;
        }
        CancelPageInput();
        return false;
    }

    internal void CancelPageInput()
    {
        pageInput.Clear();
        pageHint.Visible = false;
    }

    internal async Task<PdfPageViewState?> CapturePageViewAsync()
    {
        if (!ready || web.CoreWebView2 is null) return null;
        try
        {
            return await ReadPageViewAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    private async Task<PdfPageViewState?> ReadPageViewAsync()
    {
        for (int attempt = 0; attempt < 10 && !IsDisposed && ready; attempt++)
        {
            var result = await PdfViewportBridge.EvaluateAsync(web.CoreWebView2, """
                (() => {
                    const v = window.viewer?.viewport_;
                    if (!v || !v.documentDimensions_) return null;
                    const page = v.getMostVisiblePage();
                    const rect = v.getPageScreenRect(page);
                    return {page, width: rect.width * devicePixelRatio};
                })()
                """);
            if (result is { ValueKind: System.Text.Json.JsonValueKind.Object } value)
            {
                int page = value.GetProperty("page").GetInt32();
                double width = value.GetProperty("width").GetDouble();
                if (page >= 0 && double.IsFinite(width) && width > 0) return new(page, width);
            }
            await Task.Delay(100);
        }
        return null;
    }

    private void UpdatePageHint(string? error = null)
    {
        pageHint.Text = error ?? $"移動先: {pageInput.Text} ページ   Enterで移動 / Escで取消";
        pageHint.Visible = pageInput.IsActive;
        PositionPageHint();
        pageHint.BringToFront();
    }

    private void PositionPageHint() => pageHint.Location = new Point(Math.Max(8, ClientSize.Width - pageHint.Width - 16), 12);

    private void GoToPage(int page)
    {
        if (IsDisposed || Disposing || !ready) return;
        if (PageJumpRequested is not null) { PageJumpRequested.Invoke(page); return; }
        NavigateDocument(document, page);
    }

    internal void NavigateDocument(PdfDocument targetDocument, int page = 1)
    {
        if (IsDisposed || Disposing) return;
        document = targetDocument;
        initialPage = page;
        CancelPageInput();
        if (web.CoreWebView2 is null) return;
        ready = false;
        // The built-in PDF viewer ignores fragment-only changes after loading.
        // A fresh document URI makes it honor the page parameter on every jump.
        var target = new UriBuilder(document.Uri)
        {
            Query = $"sarupdf_jump={Guid.NewGuid():N}",
            Fragment = $"toolbar=0&page={page}"
        }.Uri;
        web.CoreWebView2.Navigate(target.AbsoluteUri);
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
