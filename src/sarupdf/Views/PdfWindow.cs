using SaruPdf.Documents;
using SaruPdf.WindowsIntegration;
using SaruPdf.Updates;

namespace SaruPdf.Views;

internal sealed class PdfWindow : Form
{
    private readonly Panel content = new() { Dock = DockStyle.Fill };
    private readonly TextBox pathBox = new()
    {
        Name = "AddressBar", Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.FixedSingle,
        BackColor = Color.White, ForeColor = Color.FromArgb(65, 74, 90),
        AccessibleName = "表示中の PDF のフルパス"
    };
    private readonly Panel errorPanel = new() { Dock = DockStyle.Top, Height = 108, Visible = false, Padding = new Padding(12), BackColor = Color.FromArgb(255, 241, 224) };
    private readonly Panel searchPanel = new() { Name = "SearchPanel", Dock = DockStyle.Top, Visible = false, Padding = new Padding(8, 4, 8, 4), BackColor = Color.FromArgb(241, 243, 247) };
    private readonly TextBox searchBox = new() { Name = "SearchText", Dock = DockStyle.Fill, AccessibleName = "検索する文字列" };
    private readonly Label searchStatus = new() { Name = "SearchStatus", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, AutoEllipsis = true };
    private readonly Button searchNext = new() { Name = "SearchNext", Text = "次を検索", Dock = DockStyle.Fill, AutoSize = false };
    private readonly Button searchPrevious = new() { Name = "SearchPrevious", Text = "前を検索", Dock = DockStyle.Fill, AutoSize = false };
    private IReadOnlyList<PdfSearchMatch> searchMatches = [];
    private string? searchTerm;
    private int searchIndex = -1, searchRevision = -1, searchGeneration;
    private bool searchBusy;
    private readonly TextBox errorText = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, BackColor = Color.FromArgb(255, 241, 224), AccessibleName = "エラー" };
    private PdfView? viewer;
    private readonly string? userDataFolder;
    private readonly PdfPageClipboard pageClipboard;
    private PdfDocument? document;
    private PdfEditor? editor;
    private PdfCommentEditor? commentEditor;
    private int? appliedTitleMode;
    private bool closingForUpdate;
    private PdfSourceWatcher? sourceWatcher;
    private PdfSourceSnapshot? pendingSource;
    private bool reloadingSource, sourceReloadQueued, externalNotice;
    private bool capturingUpdate, enabledBeforeUpdate;
    private Task loadingMode = Task.CompletedTask;
    private PdfEditSession? ActiveSession => editor?.Session ?? commentEditor?.Session;
    private readonly ToolStripMenuItem editMode = new("ページ操作モード(&M)") { Name = "EditMode", Enabled = false };
    private readonly ToolStripMenuItem viewMode = new("Viewモード(&V)") { Name = "ViewMode", Checked = true };
    private readonly ToolStripMenuItem commentMode = new("コメントモード(&C)") { Name = "CommentMode", Enabled = false };
    private readonly ToolStripMenuItem cutPage = new("ページを切り取り(&T)") { ShortcutKeys = Keys.Control | Keys.X, Enabled = false };
    private readonly ToolStripMenuItem copyPage = new("ページをコピー(&C)") { ShortcutKeys = Keys.Control | Keys.C, Enabled = false };
    private readonly ToolStripMenuItem pastePage = new("ページを貼り付け(&P)") { ShortcutKeys = Keys.Control | Keys.V, Enabled = false };
    private readonly ToolStripMenuItem rotateRight = new("右に90度(&R)") { ShortcutKeys = Keys.Control | Keys.R, Enabled = false };
    private readonly ToolStripMenuItem rotateLeft = new("左に90度(&L)") { ShortcutKeys = Keys.Control | Keys.L, Enabled = false };
    private readonly ToolStripMenuItem selectPages = new("全部選択(&A)") { ShortcutKeys = Keys.Control | Keys.A, Enabled = false };
    private readonly ToolStripMenuItem undoEdit = new("元に戻す(&U)") { ShortcutKeys = Keys.Control | Keys.Z, Enabled = false };
    private readonly ToolStripMenuItem redoEdit = new("やり直し(&R)") { ShortcutKeys = Keys.Control | Keys.Y, Enabled = false };
    private readonly ToolStripMenuItem save = new("保存(&S)") { ShortcutKeys = Keys.Control | Keys.S, Enabled = false };
    private readonly ToolStripMenuItem saveAs = new("名前を付けて保存(&A)…") { ShortcutKeys = Keys.Control | Keys.Shift | Keys.S, Enabled = false };
    private readonly ToolStripMenuItem openLocation = new("ファイルの場所を開く(&O)") { Name = "OpenFileLocation", Enabled = false };
    private PdfView? ActiveViewer => editor is null && commentEditor is null ? viewer : null;

    public event Action<string[]>? OpenRequested;
    internal event Action? UpdateRequested;
    internal string? OpenDocumentPath => document?.Path;
    public event Action<PdfDocument>? DocumentPathChanged;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<string, bool>? CanUseDocumentPath { get; set; }

    public PdfWindow(string? userDataFolder = null, PdfPageClipboard? pageClipboard = null)
    {
        this.userDataFolder = userDataFolder;
        this.pageClipboard = pageClipboard ?? new PdfPageClipboard();
        Text = "SaruPDF";
        ClientSize = new Size(1080, 800);
        MinimumSize = new Size(560, 420);
        StartPosition = FormStartPosition.WindowsDefaultLocation;
        BackColor = Color.White;
        AppIcon.Apply(this);
        var menu = new MenuStrip { Padding = new Padding(8, 5, 8, 5), BackColor = Color.FromArgb(247, 248, 251) };
        var file = new ToolStripMenuItem("ファイル(&F)");
        file.DropDownItems.Add(new ToolStripMenuItem("PDF を開く(&O)…", null, (_, _) => SelectFiles()) { ShortcutKeys = Keys.Control | Keys.O });
        file.DropDownItems.Add(save);
        file.DropDownItems.Add(saveAs);
        save.Click += (_, _) => SaveEdits(false);
        saveAs.Click += (_, _) => SaveEdits(true);
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(new ToolStripMenuItem("このウィンドウを閉じる(&W)", null, (_, _) => Close()) { ShortcutKeys = Keys.Control | Keys.W });
        menu.Items.Add(file);
        var edit = new ToolStripMenuItem("編集(&E)");
        var find = new ToolStripMenuItem("検索(&F)…") { ShortcutKeys = Keys.Control | Keys.F };
        find.Click += (_, _) => OpenSearch();
        edit.DropDownItems.AddRange([find, new ToolStripSeparator(), viewMode, editMode, commentMode, new ToolStripSeparator(), cutPage, copyPage, pastePage, selectPages, new ToolStripSeparator(), rotateRight, rotateLeft, new ToolStripSeparator(), undoEdit, redoEdit]);
        viewMode.Click += (_, _) => { if (editor is not null) ToggleEditMode(); else if (commentEditor is not null) ToggleCommentMode(); };
        editMode.Click += (_, _) => { if (editor is null) ToggleEditMode(); };
        commentMode.Click += (_, _) => { if (commentEditor is null) ToggleCommentMode(); };
        cutPage.Click += (_, _) => editor?.Cut();
        copyPage.Click += (_, _) => editor?.Copy();
        pastePage.Click += (_, _) => editor?.Paste();
        rotateRight.Click += (_, _) => editor?.Rotate(90);
        rotateLeft.Click += (_, _) => editor?.Rotate(-90);
        selectPages.Click += (_, _) => editor?.Viewer.SelectAll();
        undoEdit.Click += (_, _) => { if (commentEditor is not null) commentEditor.Undo(); else editor?.Undo(); };
        redoEdit.Click += (_, _) => { if (commentEditor is not null) commentEditor.Redo(); else editor?.Redo(); };
        menu.Items.Add(edit);
        var display = new ToolStripMenuItem("表示(&V)") { Name = "ViewMenu" };
        foreach (var (text, name, command) in new[]
        {
            ("ページ幅を基準に表示(&W)", "FitWidth", PdfViewCommand.FitWidth),
            ("1ページ(&P)", "FitPage", PdfViewCommand.FitPage),
            ("ズームイン(&I)", "ZoomIn", PdfViewCommand.ZoomIn),
            ("ズームアウト(&O)", "ZoomOut", PdfViewCommand.ZoomOut)
        })
        {
            var item = new ToolStripMenuItem(text) { Name = name };
            item.Click += async (_, _) =>
            {
                try
                {
                    if (SearchCanvas is { } canvas) canvas.ChangeView(command);
                    else if (ActiveViewer is { } active) await active.ChangeViewAsync(command);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { ShowError("表示を変更できませんでした。\n" + ex.Message); }
            };
            display.DropDownItems.Add(item);
        }
        display.DropDownOpening += (_, _) =>
        {
            foreach (ToolStripItem item in display.DropDownItems) item.Enabled = document is not null;
        };
        menu.Items.Add(display);
        var tools = new ToolStripMenuItem("ツール(&T)") { Name = "ToolsMenu" };
        openLocation.Click += (_, _) =>
        {
            if (document is null) return;
            try { ExplorerLocation.Open(document.Path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
            { ShowError("エクスプローラーでファイルの場所を開けませんでした。\n" + ex.Message); }
        };
        tools.DropDownItems.Add(openLocation); menu.Items.Add(tools);
        var help = new ToolStripMenuItem("ヘルプ(&H)") { Name = "HelpMenu" };
        help.DropDownItems.Add(new ToolStripMenuItem("バージョン情報(&A)…", null, (_, _) =>
        {
            using var dialog = new PdfAboutDialog(); dialog.ShowDialog(this);
        }) { Name = "AboutApplication" });
        help.DropDownItems.Add(new ToolStripMenuItem("更新(&U)…", null, (_, _) => UpdateRequested?.Invoke()) { Name = "UpdateApplication" });
        menu.Items.Add(help);
        MainMenuStrip = menu;

        var dismiss = new Button { Text = "閉じる", Dock = DockStyle.Right, Width = 76, AccessibleName = "エラー表示を閉じる" };
        dismiss.Click += (_, _) => errorPanel.Visible = false;
        errorPanel.Controls.Add(errorText);
        errorPanel.Controls.Add(dismiss);

        var addressBar = new Panel { Dock = DockStyle.Top, Height = Font.Height + 14, Padding = new Padding(8, 5, 8, 5), BackColor = Color.FromArgb(247, 248, 251) };
        addressBar.Controls.Add(pathBox);

        searchPanel.Height = Font.Height + 20;
        var searchLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 1, Margin = Padding.Empty };
        searchLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        searchLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
        searchLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
        searchLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        searchLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34));
        var closeSearch = new Button { Name = "SearchClose", Text = "×", Dock = DockStyle.Fill, AccessibleName = "検索を閉じる" };
        searchLayout.Controls.Add(searchBox, 0, 0); searchLayout.Controls.Add(searchPrevious, 1, 0);
        searchLayout.Controls.Add(searchNext, 2, 0); searchLayout.Controls.Add(searchStatus, 3, 0); searchLayout.Controls.Add(closeSearch, 4, 0);
        searchPanel.Controls.Add(searchLayout);
        searchBox.TextChanged += (_, _) => ResetSearch();
        searchNext.Click += async (_, _) => await FindNextAsync(false);
        searchPrevious.Click += async (_, _) => await FindNextAsync(true);
        closeSearch.Click += (_, _) => CloseSearch();

        var welcome = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(32) };
        welcome.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        welcome.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
        welcome.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        welcome.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        welcome.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        welcome.Controls.Add(new Label { Text = "SaruPDF", AutoSize = true, Font = new Font(Font.FontFamily, 30, FontStyle.Bold), ForeColor = Color.FromArgb(34, 53, 91), Anchor = AnchorStyles.None }, 0, 1);
        welcome.Controls.Add(new Label { Text = "PDF ごとに、ひとつのウィンドウ。", AutoSize = true, ForeColor = Color.FromArgb(94, 104, 122), Anchor = AnchorStyles.None }, 0, 2);
        var open = new Button { Text = "PDF を開く   Ctrl+O", AutoSize = true, Padding = new Padding(22, 10, 22, 10), Anchor = AnchorStyles.None, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(37, 77, 137), ForeColor = Color.White };
        open.Click += (_, _) => SelectFiles();
        welcome.Controls.Add(open, 0, 3);
        content.Controls.Add(welcome);
        Controls.Add(content);
        Controls.Add(errorPanel);
        Controls.Add(searchPanel);
        Controls.Add(addressBar);
        Controls.Add(menu);
        Shown += async (_, _) => { if (viewer is not null) await viewer.LoadAsync(); };
        Deactivate += (_, _) => CancelPageInput();
    }

    public void SetDocument(PdfDocument document)
    {
        if (viewer is not null || editor is not null || commentEditor is not null) throw new InvalidOperationException("このウィンドウには既に PDF が割り当てられています。");
        this.document = document;
        Text = $"{document.FileName}（{document.Path}）";
        pathBox.Text = document.Path;
        openLocation.Enabled = true;
        foreach (Control child in content.Controls) child.Dispose();
        content.Controls.Clear();
        CreateViewer(document);
        editMode.Enabled = true;
        commentMode.Enabled = true;
        if (Visible) _ = viewer!.LoadAsync();
        WatchSource();
    }

    private void WatchSource()
    {
        sourceWatcher?.Dispose(); sourceWatcher = null; pendingSource = null;
        if (document is null) return;
        try
        {
            var watcher = new PdfSourceWatcher(document.Path); sourceWatcher = watcher;
            void Post(Action action)
            {
                if (IsDisposed || !IsHandleCreated) return;
                try { BeginInvoke(() => { if (!IsDisposed && ReferenceEquals(sourceWatcher, watcher)) action(); }); }
                catch (InvalidOperationException) { }
            }
            watcher.Changed += snapshot => Post(() =>
            {
                if (ActiveSession?.MatchesSource(snapshot.Hash) == true)
                {
                    pendingSource = null;
                    if (externalNotice) { errorPanel.Visible = false; externalNotice = false; }
                    return;
                }
                pendingSource = snapshot;
                if (ActiveSession?.IsDirty == true)
                {
                    externalNotice = true;
                    ShowError("PDFが外部で変更されました。未保存の編集を保護するため再読み込みを保留しています。編集内容を残す場合は「名前を付けて保存」を使用してください。");
                }
                QueueSourceReload();
            });
            watcher.Failed += _ => Post(() =>
            {
                externalNotice = true;
                ShowError("PDFの変更を検知しましたが、読み取れませんでした。現在の表示を保持しています。ファイルの保存完了やアクセス権を確認してください。");
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { ShowError("PDFの変更監視を開始できませんでした。\n" + ex.Message); }
    }
    private void QueueSourceReload()
    {
        if (IsDisposed || !IsHandleCreated || capturingUpdate || pendingSource is null || sourceReloadQueued || reloadingSource) return;
        sourceReloadQueued = true;
        try { BeginInvoke(async () => { sourceReloadQueued = false; if (!IsDisposed) await ReloadSourceAsync(); }); }
        catch (InvalidOperationException) { sourceReloadQueued = false; }
    }
    private async Task ReloadSourceAsync()
    {
        if (capturingUpdate || document is null || viewer is null || pendingSource is not { } snapshot || ActiveSession?.IsDirty == true ||
            editor is { Visible: false } || commentEditor is { Visible: false }) return;
        reloadingSource = true;
        try
        {
            var state = SearchCanvas?.CapturePageView() ?? await viewer.CapturePageViewAsync();
            if (IsDisposed || !ReferenceEquals(pendingSource, snapshot) || ActiveSession?.IsDirty == true) return;
            // Construct the replacement before releasing the existing session.
            _ = DocumentPath.Open(snapshot.Path);
            PdfEditSession? session = ActiveSession is null ? null : new PdfEditSession(snapshot.Path, snapshot.Bytes);
            bool pages = editor is not null, comments = commentEditor is not null;
            pendingSource = null; ResetSearch();
            if (externalNotice) { errorPanel.Visible = false; externalNotice = false; }
            if (pages)
            {
                content.Controls.Remove(editor!); editor!.Dispose(); editor = null;
                AttachEditor(session!, Task.FromResult(state));
            }
            else if (comments)
            {
                content.Controls.Remove(commentEditor!); commentEditor!.Dispose(); commentEditor = null;
                AttachCommentEditor(session!, Task.FromResult(state));
            }
            else viewer.NavigateDocument(document, (state?.Page ?? 0) + 1, state);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!IsDisposed && ReferenceEquals(pendingSource, snapshot)) pendingSource = null;
            if (!IsDisposed) ShowError("PDFを再読み込みできませんでした。\n" + ex.Message);
        }
        finally { reloadingSource = false; QueueSourceReload(); }
    }

    private void CreateViewer(PdfDocument document)
    {
        viewer = new PdfView(document, userDataFolder) { Dock = DockStyle.Fill };
        viewer.OpenRequested += path => OpenRequested?.Invoke([path]);
        viewer.OpenDialogRequested += SelectFiles;
        viewer.CloseRequested += Close;
        viewer.CommandKeyRequested += TryHandleEditKey;
        viewer.FindStatusChanged += (index, count) => { if (searchPanel.Visible && ActiveViewer == viewer && searchTerm is not null) ShowSearchStatus(index, count); };
        content.Controls.Add(viewer);
    }

    private void AttachEditor(PdfEditSession session, Task<PdfPageViewState?> viewState)
    {
        editor = new PdfEditor(session, pageClipboard);
        editor.StateChanged += UpdateEditMenus;
        editor.Failed += ex => ShowError("ページを編集できませんでした。\n" + ex.Message);
        editor.ImportRequested += SelectImportPdfs;
        editor.Visible = false;
        content.Controls.Add(editor); editor.BringToFront();
        UpdateEditMenus();
        loadingMode = LoadEditorAsync(editor, viewState);
    }

    private async Task LoadEditorAsync(PdfEditor target, Task<PdfPageViewState?> viewState)
    {
        var state = await viewState;
        if (IsDisposed || target.IsDisposed || !ReferenceEquals(editor, target)) return;
        if (state is not null) state = state with { Page = Math.Clamp(state.Page, 0, target.Session.PageCount - 1) };
        target.Viewer.InheritView(state);
        await target.LoadAsync();
        if (IsDisposed || target.IsDisposed || !ReferenceEquals(editor, target)) return;
        if (viewer is not null) viewer.Visible = false;
        target.Visible = true;
        target.BringToFront();
        // Loading while hidden can leave WinForms' scroll range at an earlier
        // partial page layout. Recalculate it after the surface becomes visible.
        target.Viewer.PerformLayout();
        if (state is not null) target.Viewer.RestorePosition(state);
        QueueSourceReload();
    }

    private void SelectImportPdfs(int index)
    {
        if (editor is null || editor.Session.HasPendingCut) return;
        CancelPageInput();
        using var dialog = new OpenFileDialog
        {
            Title = "挿入するPDFを選択（全ページをインポート）", Filter = "PDF ファイル (*.pdf)|*.pdf",
            Multiselect = true, CheckFileExists = true, RestoreDirectory = true
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) editor.ImportFiles(index, dialog.FileNames);
    }

    public void ShowError(string message)
    {
        errorText.Text = message.ReplaceLineEndings(Environment.NewLine);
        errorPanel.Visible = true;
    }

    private void SelectFiles()
    {
        CancelPageInput();
        using var dialog = new OpenFileDialog
        {
            Title = "PDF を開く", Filter = "PDF ファイル (*.pdf)|*.pdf",
            Multiselect = true, CheckFileExists = true, RestoreDirectory = true
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) OpenRequested?.Invoke(dialog.FileNames);
    }

    private PdfPageCanvas? SearchCanvas => editor?.Viewer ?? (PdfPageCanvas?)commentEditor?.Viewer;
    private void OpenSearch()
    {
        CancelPageInput(); searchPanel.Visible = true; searchBox.Focus(); searchBox.SelectAll();
    }
    private void CloseSearch()
    {
        ResetSearch(); searchPanel.Visible = false;
        if (SearchCanvas is { } canvas) canvas.Focus(); else viewer?.Focus();
    }
    private void ResetSearch()
    {
        searchGeneration++; searchTerm = null; searchRevision = -1; searchIndex = -1; searchMatches = [];
        viewer?.StopFind(); SearchCanvas?.ShowSearchMatch(null); searchStatus.Text = "";
    }
    private void ShowSearchStatus(int index, int count) => searchStatus.Text = count <= 0 ? "一致なし" : $"{Math.Clamp(index, 1, count)} / {count}";
    private async Task FindNextAsync(bool previous)
    {
        if (searchBusy) return;
        string term = searchBox.Text;
        if (document is null) { searchStatus.Text = "PDFを開いてください"; return; }
        if (string.IsNullOrWhiteSpace(term)) { ResetSearch(); searchStatus.Text = "検索語を入力"; return; }
        int generation = ++searchGeneration;
        searchBusy = true; searchNext.Enabled = searchPrevious.Enabled = false; searchStatus.Text = "検索中…";
        try
        {
            if (ActiveSession is not { } session)
            {
                searchTerm = term;
                if (viewer is null || !await viewer.FindAsync(term, previous)) searchStatus.Text = "PDFを読込中";
                if (generation != searchGeneration) viewer?.StopFind();
                return;
            }
            int revision = session.Revision;
            if (searchTerm != term || searchRevision != revision)
            {
                string? snapshot = session.WritePreview();
                var found = snapshot is null ? [] : await Task.Run(() => PdfSearch.Find(PdfTextMap.Read(snapshot), term));
                if (IsDisposed || generation != searchGeneration || !ReferenceEquals(ActiveSession, session) || session.Revision != revision) return;
                searchMatches = found; searchTerm = term; searchRevision = revision;
                searchIndex = searchMatches.Count == 0 ? -1 : previous ? searchMatches.Count - 1 : 0;
            }
            else if (searchMatches.Count > 0)
                searchIndex = (searchIndex + (previous ? searchMatches.Count - 1 : 1)) % searchMatches.Count;
            SearchCanvas?.ShowSearchMatch(searchIndex < 0 ? null : searchMatches[searchIndex]);
            ShowSearchStatus(searchIndex + 1, searchMatches.Count);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!IsDisposed && generation == searchGeneration) { searchStatus.Text = "検索できません"; ShowError("PDFを検索できませんでした。\n" + ex.Message); }
        }
        finally { searchBusy = false; if (!IsDisposed) searchNext.Enabled = searchPrevious.Enabled = true; }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (searchPanel.Visible && searchBox.Focused)
        {
            if (keyData is Keys.F3 or (Keys.Shift | Keys.F3)) { _ = FindNextAsync((keyData & Keys.Shift) != 0); return true; }
            if (keyData == Keys.Enter) { _ = FindNextAsync(false); return true; }
            if (keyData == (Keys.Shift | Keys.Enter)) { _ = FindNextAsync(true); return true; }
            if (keyData == Keys.Escape) { CloseSearch(); return true; }
            if (keyData == (Keys.Control | Keys.A)) { searchBox.SelectAll(); return true; }
            if (keyData == (Keys.Control | Keys.C) || keyData == (Keys.Control | Keys.X) || keyData == (Keys.Control | Keys.V))
            {
                try
                {
                    if (keyData == (Keys.Control | Keys.C)) searchBox.Copy();
                    else if (keyData == (Keys.Control | Keys.X)) searchBox.Cut();
                    else searchBox.Paste();
                }
                catch (System.Runtime.InteropServices.ExternalException) { ShowError("クリップボードを使用できませんでした。"); }
                return true;
            }
            if (keyData == (Keys.Control | Keys.Z)) { searchBox.Undo(); return true; }
            if ((keyData & Keys.Modifiers) is Keys.None or Keys.Shift) return base.ProcessCmdKey(ref msg, keyData);
        }
        if (pathBox.Focused)
        {
            var modifiers = keyData & Keys.Modifiers;
            var key = keyData & Keys.KeyCode;
            if (modifiers == Keys.Control && key == Keys.A) { pathBox.SelectAll(); return true; }
            if (modifiers == Keys.Control && key == Keys.C)
            {
                try { if (pathBox.SelectionLength > 0) pathBox.Copy(); }
                catch (System.Runtime.InteropServices.ExternalException) { ShowError("パスをコピーできませんでした。もう一度お試しください。"); }
                return true;
            }
            if (modifiers == Keys.Control && key is Keys.X or Keys.V or Keys.Z or Keys.Y) return true;
            if (modifiers is Keys.None or Keys.Shift ||
                (modifiers == Keys.Control && key is Keys.Left or Keys.Right or Keys.Home or Keys.End))
                return base.ProcessCmdKey(ref msg, keyData);
        }
        if (TryHandleEditKey(keyData)) return true;
        if (editor?.Viewer.TryHandlePageKey(keyData) == true) return true;
        if (ActiveViewer?.TryHandlePageKey(keyData) == true) return true;
        if (keyData == (Keys.Control | Keys.O)) { SelectFiles(); return true; }
        if (keyData == (Keys.Control | Keys.W)) { Close(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    internal void ToggleEditMode()
    {
        if (document is null || viewer is null) return;
        PdfPageViewState? inherited = null;
        if (commentEditor is not null) { inherited = commentEditor.Viewer.CapturePageView(); ToggleCommentMode(); if (commentEditor is not null) return; }
        if (editor is not null)
        {
            if (!ConfirmFinishEditing()) return;
            var state = editor.Viewer.CapturePageView();
            ResetSearch();
            content.Controls.Remove(editor);
            editor.Dispose(); editor = null;
            viewer.Visible = true;
            viewer.NavigateDocument(document, (state?.Page ?? 0) + 1, state);
            _ = viewer.LoadAsync();
            UpdateEditMenus();
            QueueSourceReload();
            return;
        }
        PdfEditSession? session = null;
        try
        {
            UseWaitCursor = true;
            ResetSearch();
            viewer.CancelPageInput();
            session = new PdfEditSession(document.Path);
            AttachEditor(session, inherited is null ? viewer.CapturePageViewAsync() : Task.FromResult<PdfPageViewState?>(inherited));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (editor is not null) { content.Controls.Remove(editor); editor.Dispose(); editor = null; }
            else session?.Dispose();
            viewer.Visible = true;
            UpdateEditMenus();
            ShowError("このPDFをページ操作モードで開けませんでした。\n" + ex.Message);
        }
        finally { UseWaitCursor = false; }
    }

    internal void ToggleCommentMode()
    {
        if (document is null || viewer is null) return;
        PdfPageViewState? inherited = null;
        if (editor is not null) { inherited = editor.Viewer.CapturePageView(); ToggleEditMode(); if (editor is not null) return; }
        if (commentEditor is not null)
        {
            if (!ConfirmFinishEditing()) return;
            var state = commentEditor.Viewer.CapturePageView();
            ResetSearch();
            content.Controls.Remove(commentEditor); commentEditor.Dispose(); commentEditor = null;
            viewer.Visible = true; viewer.NavigateDocument(document, (state?.Page ?? 0) + 1, state); _ = viewer.LoadAsync();
            UpdateEditMenus(); return;
        }
        PdfEditSession? session = null;
        try
        {
            ResetSearch();
            viewer.CancelPageInput();
            session = new PdfEditSession(document.Path);
            AttachCommentEditor(session, inherited is null ? viewer.CapturePageViewAsync() : Task.FromResult<PdfPageViewState?>(inherited));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (commentEditor is not null) { content.Controls.Remove(commentEditor); commentEditor.Dispose(); commentEditor = null; }
            else session?.Dispose();
            viewer.Visible = true; UpdateEditMenus(); ShowError("コメントモードで開けませんでした。\n" + ex.Message);
        }
    }

    private void AttachCommentEditor(PdfEditSession session, Task<PdfPageViewState?> state)
    {
        commentEditor = new PdfCommentEditor(session) { Visible = false };
        commentEditor.StateChanged += UpdateEditMenus;
        commentEditor.Failed += ex => ShowError("コメントを処理できませんでした。\n" + ex.Message);
        content.Controls.Add(commentEditor); UpdateEditMenus();
        loadingMode = LoadCommentEditorAsync(commentEditor, state);
    }

    private async Task LoadCommentEditorAsync(PdfCommentEditor target, Task<PdfPageViewState?> viewState)
    {
        var state = await viewState;
        if (IsDisposed || target.IsDisposed || !ReferenceEquals(commentEditor, target)) return;
        if (state is not null) state = state with { Page = Math.Clamp(state.Page, 0, target.Session.PageCount - 1) };
        await target.LoadAsync(state);
        if (IsDisposed || target.IsDisposed || !ReferenceEquals(commentEditor, target)) return;
        if (viewer is not null) viewer.Visible = false;
        target.Visible = true; target.BringToFront(); target.Viewer.PerformLayout(); target.Viewer.Focus();
        if (state is not null) target.Viewer.RestorePosition(state);
        QueueSourceReload();
    }

    private void UpdateEditMenus()
    {
        if (pendingSource is not null && ActiveSession?.IsDirty != true) QueueSourceReload();
        if (searchRevision >= 0 && ActiveSession is { } session && session.Revision != searchRevision) ResetSearch();
        editMode.Checked = editor is not null;
        commentMode.Checked = commentEditor is not null;
        viewMode.Checked = editor is null && commentEditor is null;
        UpdateTitleBar();
        cutPage.Enabled = editor?.CanCut == true;
        copyPage.Enabled = editor?.CanCopy == true;
        pastePage.Enabled = editor?.CanPaste == true;
        rotateRight.Enabled = rotateLeft.Enabled = editor?.CanRotate == true;
        selectPages.Enabled = editor?.Session.PageCount > 0;
        undoEdit.Enabled = ActiveSession?.CanUndo == true;
        redoEdit.Enabled = ActiveSession?.CanRedo == true;
        save.Enabled = ActiveSession is { PageCount: > 0, HasPendingCut: false, IsDirty: true };
        saveAs.Enabled = ActiveSession is { PageCount: > 0, HasPendingCut: false };
    }

    private bool TryHandleEditKey(Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.F)) { BeginInvoke(OpenSearch); return true; }
        if (searchPanel.Visible && keyData is Keys.F3 or (Keys.Shift | Keys.F3))
        { BeginInvoke(async () => await FindNextAsync((keyData & Keys.Shift) != 0)); return true; }
        if (keyData == (Keys.Alt | Keys.D))
        {
            // WebView2 keyboard callbacks must return before changing focus.
            BeginInvoke(() =>
            {
                if (IsDisposed) return;
                CancelPageInput();
                pathBox.Focus();
                pathBox.SelectAll();
            });
            return true;
        }
        if (commentEditor is not null)
        {
            if (commentEditor.Viewer.TryHandleCaretKey(keyData) || commentEditor.Viewer.TryHandleZoomKey(keyData)) return true;
            Action? commentCommand = keyData switch
            {
                Keys.Control | Keys.Z => () => commentEditor?.Undo(),
                Keys.Control | Keys.Y => () => commentEditor?.Redo(),
                Keys.Control | Keys.S => () => SaveEdits(false),
                Keys.Control | Keys.Shift | Keys.S => () => SaveEdits(true),
                _ => null
            };
            if (commentCommand is null) return false;
            BeginInvoke(() => { if (!IsDisposed) commentCommand(); }); return true;
        }
        if (editor is null) return false;
        if (editor.Viewer.TryHandlePageMoveKey(keyData)) return true;
        if (editor.Viewer.TryHandleZoomKey(keyData)) return true;
        Action? command = keyData switch
        {
            Keys.Control | Keys.X => () => editor?.Cut(),
            Keys.Control | Keys.C => () => editor?.Copy(),
            Keys.Control | Keys.V => () => editor?.Paste(),
            Keys.Control | Keys.R => () => editor?.Rotate(90),
            Keys.Control | Keys.L => () => editor?.Rotate(-90),
            Keys.Control | Keys.Z => () => editor?.Undo(),
            Keys.Control | Keys.Y => () => editor?.Redo(),
            Keys.Control | Keys.A => () => editor?.Viewer.SelectAll(),
            Keys.Control | Keys.S => () => SaveEdits(false),
            Keys.Control | Keys.Shift | Keys.S => () => SaveEdits(true),
            _ => null
        };
        if (command is null) return false;
        CancelPageInput();
        // WebView2 keyboard callbacks must return before navigation or dialogs.
        BeginInvoke(() => { if (!IsDisposed) command(); });
        return true;
    }

    private void CancelPageInput()
    {
        editor?.Viewer.CancelPageInput();
        viewer?.CancelPageInput();
    }

    private bool SaveEdits(bool choosePath)
    {
        var session = ActiveSession;
        if (session is null) return false;
        if (session.HasPendingCut)
        {
            ShowError("切り取り中のページを貼り付けるか、元に戻してから保存してください。");
            return false;
        }
        if (session.PageCount == 0)
        {
            ShowError("ページを貼り付けるかPDFをインポートしてから保存してください。"); return false;
        }
        if (!choosePath && !session.IsDirty) return true;
        string? path = document?.Path;
        if (choosePath || path is null)
        {
            using var dialog = new SaveFileDialog
            {
                Title = "変更したPDFを保存", Filter = "PDF ファイル (*.pdf)|*.pdf", DefaultExt = "pdf",
                AddExtension = true, FileName = document?.FileName ?? "無題.pdf",
                InitialDirectory = document is null ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) : Path.GetDirectoryName(document.Path),
                OverwritePrompt = true
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return false;
            path = Path.GetFullPath(dialog.FileName);
        }
        return SaveToPath(path!);
    }

    internal bool SaveToPath(string path)
    {
        var session = ActiveSession;
        if (session is null) return false;
        try
        {
            path = Path.GetFullPath(path);
            if (CanUseDocumentPath?.Invoke(path) == false)
            {
                ShowError("保存先のPDFは別のウィンドウで開かれています。そのウィンドウを閉じるか、別の名前で保存してください。");
                return false;
            }
            UseWaitCursor = true;
            session.Save(path);
            document = DocumentPath.Open(path);
            WatchSource();
            if (viewer is null) { CreateViewer(document); viewer!.Visible = false; }
            editMode.Enabled = true;
            pathBox.Text = document.Path;
            Text = $"{document.FileName}（{document.Path}）";
            DocumentPathChanged?.Invoke(document);
            editor?.UpdateState(); commentEditor?.UpdateState();
            errorPanel.Visible = false;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { ShowError("PDFを保存できませんでした。編集内容は保持しています。\n" + ex.Message); return false; }
        finally { UseWaitCursor = false; }
    }

    private bool ConfirmFinishEditing()
    {
        if (ActiveSession?.IsDirty != true) return true;
        var answer = MessageBox.Show(this, "PDFの変更内容を保存しますか？\n「いいえ」を選ぶと、未保存の変更を破棄します。", "PDFの変更", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        return answer == DialogResult.No || (answer == DialogResult.Yes && SaveEdits(false));
    }

    internal bool PrepareForUpdate() => ConfirmFinishEditing();
    internal void FreezeForUpdate(bool freeze)
    {
        if (freeze) { enabledBeforeUpdate = Enabled; capturingUpdate = true; Enabled = false; }
        else { capturingUpdate = false; Enabled = enabledBeforeUpdate; QueueSourceReload(); }
    }
    internal async Task<PdfWindowState> CaptureForUpdateAsync()
    {
        await loadingMode;
        var view = SearchCanvas?.CapturePageView() ?? (viewer is null ? null : await viewer.CapturePageViewAsync());
        var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        var selection = commentEditor?.Viewer.Selection;
        return new(document?.Path, editor is not null ? PdfDisplayMode.Pages : commentEditor is not null ? PdfDisplayMode.Comments : PdfDisplayMode.View,
            view, new(bounds.X, bounds.Y, bounds.Width, bounds.Height, WindowState == FormWindowState.Maximized), selection?.Caret, selection?.Anchor);
    }
    internal void RestorePlacement(WindowPlacement? placement)
    {
        if (!TopLevel || placement is not { } p) return;
        var bounds = new Rectangle(p.X, p.Y, p.Width, p.Height);
        if (Screen.AllScreens.Any(screen => Rectangle.Intersect(screen.WorkingArea, bounds) is { Width: >= 80, Height: >= 80 }))
        { StartPosition = FormStartPosition.Manual; Bounds = bounds; }
        if (p.Maximized) WindowState = FormWindowState.Maximized;
    }
    internal async Task RestoreAfterUpdateAsync(PdfWindowState state)
    {
        if (document is null || viewer is null || editor is not null || commentEditor is not null) return;
        if (state.Mode == PdfDisplayMode.View) { viewer.NavigateDocument(document, (state.View?.Page ?? 0) + 1, state.View); return; }
        var session = new PdfEditSession(document.Path);
        ResetSearch();
        if (state.Mode == PdfDisplayMode.Pages) AttachEditor(session, Task.FromResult(state.View));
        else AttachCommentEditor(session, Task.FromResult(state.View));
        await loadingMode;
        if (IsDisposed) return;
        if (commentEditor?.Viewer.Selection is { } selection && state.Caret is { } caret)
        {
            selection.Set(state.Anchor ?? caret, false); selection.Set(caret, true); commentEditor.Viewer.Invalidate();
        }
    }
    internal void CloseForUpdate() { closingForUpdate = true; Close(); }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { sourceWatcher?.Dispose(); sourceWatcher = null; }
        base.Dispose(disposing);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!closingForUpdate && !ConfirmFinishEditing()) e.Cancel = true;
        base.OnFormClosing(e);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        appliedTitleMode = null;
        UpdateTitleBar();
    }

    private void UpdateTitleBar()
    {
        int mode = editor is not null ? 1 : commentEditor is not null ? 2 : 0;
        if (!IsHandleCreated || appliedTitleMode == mode) return;
        NativeWindows.SetTitleBarColor(this, mode == 1 ? NativeWindows.PageOperationTitleColor : mode == 2 ? Color.FromArgb(0, 110, 65) : null);
        appliedTitleMode = mode;
    }
}
