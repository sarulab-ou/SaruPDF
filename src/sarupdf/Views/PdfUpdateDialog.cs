using SaruPdf.Updates;

namespace SaruPdf.Views;

internal sealed class PdfUpdateDialog : Form
{
    private readonly GitHubUpdater updater;
    private readonly Version current;
    private readonly string downloadRoot;
    private readonly CancellationTokenSource cancellation = new(TimeSpan.FromMinutes(5));
    private readonly Label status = new() { Name = "UpdateStatus", Dock = DockStyle.Fill, AutoSize = false };
    private readonly ProgressBar progress = new() { Name = "UpdateProgress", Dock = DockStyle.Fill, Style = ProgressBarStyle.Marquee };
    private readonly Button download = new() { Name = "DownloadUpdate", Text = "ダウンロードして更新", AutoSize = true, Enabled = false };
    private UpdateRelease? release;
    private bool resourcesDisposed;
    internal string? InstallerPath { get; private set; }

    internal PdfUpdateDialog(GitHubUpdater? updater = null, Version? current = null, string? downloadRoot = null)
    {
        this.updater = updater ?? new GitHubUpdater(); this.current = current ?? GitHubUpdater.CurrentVersion;
        this.downloadRoot = downloadRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SaruPDF", "Updates");
        Text = "SaruPDF 更新"; ClientSize = new Size(510, 220); FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false; StartPosition = FormStartPosition.CenterParent; AppIcon.Apply(this);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 25)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var close = new Button { Text = "閉じる", Name = "CloseUpdate", AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(close); buttons.Controls.Add(download); CancelButton = close;
        layout.Controls.Add(status, 0, 0); layout.Controls.Add(progress, 0, 1); layout.Controls.Add(buttons, 0, 2); Controls.Add(layout);
        status.Text = $"現在のバージョン: {this.current}\nGitHubで最新版を確認しています…";
        Shown += async (_, _) => await CheckAsync();
        download.Click += async (_, _) => await DownloadAsync();
        FormClosing += (_, _) => cancellation.Cancel();
    }
    private async Task CheckAsync()
    {
        try
        {
            release = await updater.CheckAsync(current, cancellation.Token);
            if (IsDisposed) return;
            progress.Style = ProgressBarStyle.Continuous;
            status.Text = release is null ? $"現在のバージョン: {current}\n最新版を使用しています。" :
                $"現在のバージョン: {current}　最新: {release.Version}\n\n更新時にすべてのPDFウィンドウを閉じ、インストーラーを起動します。未保存の変更は終了前に確認します。";
            download.Enabled = release is not null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Failure(ex); }
    }
    private async Task DownloadAsync()
    {
        if (release is null) return;
        download.Enabled = false; progress.Style = ProgressBarStyle.Continuous;
        status.Text = $"SaruPDF {release.Version} をダウンロードしています…";
        try
        {
            var report = new Progress<int>(value => { if (!IsDisposed) progress.Value = Math.Clamp(value, 0, 100); });
            InstallerPath = await updater.DownloadAsync(release, downloadRoot, report, cancellation.Token);
            if (IsDisposed) return;
            DialogResult = DialogResult.OK; Close();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Failure(ex); }
    }
    private void Failure(Exception ex)
    {
        if (IsDisposed) return;
        progress.Style = ProgressBarStyle.Continuous; progress.Value = 0;
        status.Text = ex is OperationCanceledException ? "更新を中止しました。または通信がタイムアウトしました。" : "更新を確認・取得できませんでした。\n" + ex.Message;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !resourcesDisposed) { resourcesDisposed = true; cancellation.Cancel(); cancellation.Dispose(); updater.Dispose(); }
        base.Dispose(disposing);
    }
}
