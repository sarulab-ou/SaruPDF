[README に戻る](../README.md)

# 開発・ビルドガイド

必要環境は Windows x64、.NET **10.0.403 SDK**（`global.json`、同じ feature band の新しいパッチも許可）、PowerShell 7 以降、初回復元用のインターネット接続です。Windows Forms のためビルド・GUI 確認は Windows 上で実行します。Git を使用する場合はこの作業環境のルールに従い WSL Git を使います。

SDK 未導入の場合は次を実行できます。Microsoft 公式 ZIP と公式メタデータの SHA512 を照合し、`.tools/dotnet` にだけ展開します。システムの SDK や PATH は変更しません。

```powershell
.\scripts\Install-Sdk.ps1
.\scripts\Build.ps1
.\scripts\Test.ps1
.\scripts\Publish.ps1
.\scripts\Install-InstallerTools.ps1
.\scripts\Build-Installer.ps1
```

`Build.ps1` は Release ビルド、`Test.ps1` は自作のコンソール型テストランナー、`Publish.ps1` はインストーラーの入力となる self-contained フォルダーを作成します。README、開発ガイド、検証記録、実画面の画像もこのフォルダーにコピーします。PowerShell の実行ポリシーでスクリプトが禁止されている環境では、組織のポリシーに従って実行してください。

出力先を変える場合は `scripts/Publish.ps1 -OutputRoot artifacts/release` のように指定できます。`Build-Installer.ps1` は既定でこの場所に publish と NSIS コンパイルを行います。既存の配布物を使う場合は `-SkipPublish` を付けます。

インストーラーのビルドには WSL Ubuntu 24.04 と NSIS が必要です。`Install-InstallerTools.ps1` は Ubuntu 公式の **NSIS 3.09-4ubuntu1** と `nsis-common` を `.tools/nsis-root` に展開します。`apt install`、管理者権限での Linux パッケージ変更、Windows の NSIS インストールは行いません。初回は Ubuntu 公式リポジトリへの接続が必要です。

GitHub で配布する場合は、ソースをコミット・push した後、`scripts/Publish-GitHubRelease.ps1` を実行します。WSL の GitHub CLI 認証が必要です。スクリプトは既存リリースの上書き、リポジトリの公開範囲の変更、コミットを自動で行いません。

```text
artifacts/release/SaruPDF-0.7.0-win-x64/SaruPDF.exe
artifacts/release/SaruPDF-0.7.0-Setup-x64.exe
```

NuGet は `Microsoft.Web.WebView2 1.0.4314.35` 、MIT版の `PDFsharp 6.2.4`、Apache 2.0版の `PdfPig 0.1.16` を完全固定し、`packages.lock.json` を保存しています。スクリプトは `--locked-mode` で復元します。SDK・パッケージキャッシュ・ビルド生成物・セットアップ EXE は Git 管理対象外です。依存を意図的に変更したときだけ `dotnet restore --force-evaluate` でロックを更新してください。PDFsharpとその依存ライセンスは配布フォルダーの `third-party-licenses/` に同梱します。

## 実行時のデータと関連付けコマンド

WebView2のキャッシュ等は、書き込み可能な `%LOCALAPPDATA%\SaruPDF\WebView2\Session-<ログオンセッション番号>` に保存します。実行ファイルの配置先には保存しません。アンインストール後もキャッシュは保持します。

候補登録だけを行うコマンドもあります。現在のユーザーに作用し、PDFウィンドウは開きません。通常起動やビルドのみでは登録しません。

```powershell
.\SaruPDF.exe --register-pdf
.\SaruPDF.exe --unregister-pdf
.\SaruPDF.exe --pdf-association
```

既定のPDFアプリはWindowsの設定画面で利用者が選択します。SaruPDFは既存の`.pdf`の既定値や`UserChoice`を書き換えません。

## 構成と多重起動

- `Documents/DocumentPath.cs`: パス正規化、読み取り・拡張子・PDF ヘッダーの確認、`System.Uri` による URI 作成。
- `Documents/WindowCatalog.cs`: UI スレッドが所有する文書とウィンドウの対応表。
- `Instances/`: ユーザー／ログオンセッション単位の名前付き Mutex と同一ユーザー限定の名前付きパイプ。長さ付き UTF-8 JSON、最大 1 MiB・256 パス、タイムアウト付きです。
- `PdfApplicationContext.cs`: メインウィンドウを固定しないアプリ寿命管理。パイプ要求を UI スレッドへ渡し、ウィンドウ登録後に ACK を返します。同じ要求 ID の再送は直近 128 件を除重します。
- `Views/`: 通常の Windows Forms ウィンドウと非同期 WebView2 表示。初期化／読み込み完了からの前面化処理はありません。
- `Documents/PdfEditSession.cs` / `Views/PdfEditor.cs` / `Views/PdfPageCanvas.cs`: 元のPDFのスナップショットとページ順・切り取り中の複数ページ・Undo/Redoを管理し、PDF本文を直接選択する連続ページ画面を表示します。
- `Documents/PdfPageClipboard.cs`: コピー対象を独立したPDFデータにし、アプリケーションコンテキストが所有するクリップボードを全PDFウィンドウで共有します。Windowsのテキスト用クリップボードは使用しません。
- `WindowsIntegration/`: HKCU の ProgID、OpenWithProgids、Capabilities、RegisteredApplications の登録・所有者を確認した解除と、Windows 設定への遷移。
- `installer/SaruPDF.nsi`: ユーザー単位の導入、関連付け候補の任意登録、アンインストーラー。削除対象はビルド時に配布ファイルから生成し、インストール先の再帰削除は行いません。

追加プロセス側で相対パスを解決し、既存プロセスの PID に `AllowSetForegroundWindow` を呼んでから要求を送信します。終了中で受け付けられない場合や ACK が失われた場合は再試行し、旧プロセス終了後には自分が主プロセスになります。引き継ぎは最大約 20 秒でエラー表示します。最後の閉鎖から 200 ms の間に届いた要求は既存プロセスが処理できます。OS が前面化を認めない場合はタスクバーを点滅させます。常時最前面にはしません。

編集時は元ファイルをメモリーへ読み、ページ順とコピーで追加したページの参照を保持します。編集画面はWindows標準の `Windows.Data.Pdf` で原本・コピーのスナップショットを描画します。ページ順の変更で描画用PDFを毎回書き直すことはせず、ページIDから表示順へ割り当てます。追加したPDFの読み込みは直列化し、非同期読み込み中は仮寸法を使用します。縦横寸法の異なるPDFを読み込んだ後も、ユーザーがその間に操作していなければ選択位置へ追従します。可視ページを非同期描画し、ズーム変更時にキャッシュを無効化します。画像1枚の描画は最大800万画素・幅3000pxに抑え、可視ページ以外のキャッシュを制限します。

ページの矩形とページ間の余白を独自に計算し、Ctrl/Shift選択とコンテキストメニューの挿入位置を決めます。原本スナップショットは専用の `%TEMP%/SaruPDF-edit-<GUID>/` に保存し、終了時に自分が作ったファイルだけを削除します。描画エンジンが保持する等の理由で削除できない場合は残ることがあります。表示はラスター画像ですが、保存時は元のPDFオブジェクトを並べ替え、PDF本文を画像へ変換しません。閲覧モードは引き続きWebView2です。

TFMは `net10.0-windows10.0.19041.0`、最低OSはWindows 10 build 17763です。Windows SDK projectionは `Directory.Build.props` の `WindowsSdkPackageVersion=10.0.19041.57` で固定します。SDK／C#/WinRTのライセンスを配布物へ含め、インストーラーにSDKコンポーネントの利用条件ページを追加しています。

出力はPDFsharpのModifyモードで開いた元のカタログを保持し、`PdfPages.MovePage`でページ参照を移します。新規文書への全ページインポートは行いません。切り取り中は選択ページが一時的に表示から外れますが保存は禁止し、貼り付けまたはUndoで戻せます。切り取りは選択番号を昇順・重複なしに正規化し、複数ページの元の順序を保ちます。保存済みのページ順を別途保持するため、保存をまたぐUndo/Redoも未保存状態に反映します。

保存先と同じフォルダーに一時ファイルを書いてFlushし、ページ数を再読込で検証してから `File.Replace`（既存）または `File.Move`（新規）で置き換えます。上書き前には元ファイルのSHA-256を編集開始／直近保存時と比較します。失敗時は編集中の状態を保持し、一時ファイルを削除します。別名保存時は文書管理のパスも付け替え、別ウィンドウで開かれた保存先は拒否します。

コピーは現在のページ順で選択ページを昇順に抽出し、PDFsharpのImportモードで独立したPDFへ出力します。元ファイル・元ウィンドウを閉じても利用でき、コピー時点の未保存編集も含みます。貼り付けではコピーを消費せず、固有のページIDを割り当てるため、同じページを繰り返し貼り付けてもUndo/Redoと区別できます。取り込んだページの再コピーも可能です。保存時は貼り付け先の元カタログを保持し、追加したページをImportモードから挿入します。ページ数の検証は追加後の数に対して行います。

コピーは文書を変更しません。クリップボード変更の通知で別ウィンドウのメニューも更新し、編集画面のDispose時には通知購読を解除します。貼り付け先にローカルの切り取りがある場合はそれを優先し、コピーの挿入と混同しないようにします。

PDFインポートはページ境界の座標から挿入位置を取得し、複数選択可能なファイルダイアログを開きます。モデルはすべてのファイルを読み込んで検証した後にUndoを一度だけ記録し、全ページをまとめて挿入します。途中のファイルで失敗した場合は文書と履歴を変更しません。アプリ内のコピー用クリップボードは変更しません。

ページ操作画面のCtrl+Oemplus / Ctrl+Shift+Oemplus / Ctrl+Addを拡大、Ctrl+OemMinus / Ctrl+Subtractを縮小として処理します。拡大縮小時は画面中央にあるページ内の相対位置を維持します。

ページ操作モードに切り替える際、同じWebView2のPDFレンダラーに限定したCDPセッションから、表示ページとその実際の描画幅を取得します。`WebView2.ZoomFactor`はPDF固有の倍率ではないため使用しません。描画幅をWindows PDFページ寸法に対する倍率に換算し、ウィンドウ幅が変わっても引き継いだ倍率を維持します。倍率取得は2秒以内に制限します。Edgeの内部PDFビューアーAPIに依存し、将来のRuntimeで取得できない場合や読み込み前は幅に合わせる表示になります。

ページ操作モードでは↑／↓・PgUp／PgDnをページ単位、Home／Endを先頭・末尾への移動として処理し、Shift付きでは最後にクリック／通常移動したページを起点に選択を伸縮します。選択の起点と移動先を別々に保持し、逆方向へ移動した場合も起点を維持します。ページ間の挿入位置からの移動、先頭・末尾、切り取り後や全ページ切り取り時にも範囲外を参照しません。

現在ページは選択範囲から独立した位置で管理し、Ctrl+クリックで選択を解除した現在ページからも次へ移動できます。選択ページは青枠・選択中文字、現在ページはオレンジの内枠と表示ラベルで区別します。クリック・キー移動・数字入力・ページ順変更時にページの中心を表示領域の縦中央へ移し、短い先頭・末尾ページにも十分な余白を確保します。リサイズ後も現在ページを中央に配置します。

Windows 11では `NativeWindows.SetPageOperationTitleBar` がDWMの `DWMWA_CAPTION_COLOR`／`DWMWA_TEXT_COLOR` により、ページ操作モードのタイトルバーを青（32,88,150）・白文字にします。解除時は `DWMWA_COLOR_DEFAULT` に戻します。ハンドル再作成時にも再適用します。Windows 10では色指定に対応しないため通常のタイトルバーを使用します。[Microsoftの属性仕様](https://learn.microsoft.com/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute)

## 対応範囲・制限

数字＋Enterによるページ移動は `Views/PageNumberInput.cs` と `PdfView.TryHandlePageKey` で処理します。WebView2 の `AllowHostInputProcessing` を有効にして通常の数字キーも受け取り、常設ツールバーを追加せず一時的な案内だけを表示します。テンキー、Backspace、Escに対応し、ウィンドウの非アクティブ化で入力を取り消します。修飾キー付きの入力はこの処理で消費しません。

組み込みPDFビューアーは読み込み後のフラグメントだけの変更をページ移動として扱わないため、移動ごとに一意なクエリーを付けた同じファイルURIへナビゲートし、`toolbar=0&page=<番号>` を指定します。再表示時のズームなどはWebView2に依存します。総ページ数を解析しての上限検査は行いません。

- 同一性は `Path.GetFullPath` と大文字小文字を無視する完全パス比較です。`.` / `..` / 相対パス / 大文字小文字の差を吸収し、別フォルダーの同名 PDF は別扱いです。
- シンボリックリンク・ハードリンク・ジャンクション・8.3 短縮名・ドライブ文字と UNC 名の別名を跨ぐ同一判定は未対応です。ファイル ID はキャッシュしません。外部アプリが同じパスのファイルを置換しても、そのパスの既存ウィンドウを優先します。最新版を表示するには一度閉じて再度開いてください。大文字小文字を区別する特殊なディレクトリは対象外です。
- ページ操作は同じPDF内の並べ替えと、同じPDF・別のPDFへの複数ページコピーに対応します。PDF本文の編集・ページ削除は未実装です。コメントモードは標準注釈の追加・閲覧に対応します。暗号化PDFと署名欄のあるPDFは編集を拒否します。コピー元の文書全体のしおり・フォーム構造は移植しません。PDF/A・PDF/UA、特殊フォーム、複雑なページ間リンク、ページラベルなどの保持は未検証です。ページ操作モードの文字選択やリンク操作はありません。
- 文書の別 PDF への通常のリンクは共通の「開く」処理へ集約します。文書の置換を防ぐため外部ドラッグ＆ドロップを無効にし、同じ文書以外の内部ナビゲーションをキャンセルします。HTTP(S) 以外の外部プロトコル、非ユーザー操作の新規ウィンドウは許可しません。PDF 埋め込み添付ファイルや特殊なアクションは保証しません。
- 最初の 1 KiB に PDF ヘッダーがあるかを確認します。独自の PDF 構文解析は行わず、破損・暗号化・未対応機能は WebView2 の表示に依存します。ナビゲーション失敗はアプリ内に案内しますが、Runtime が「読み込み成功」として独自の PDF エラー画面を返す場合は、その画面で確認してください。
- 通常権限での使用を前提とします。同じユーザーでも管理者権限と通常権限を混在させるとパイプ接続が拒否され、引き継ぎエラーになります。
- EXE／インストーラーへの署名と自動更新は未対応です。Windows 10 全ビルド・別 PC での互換性は未確認です。
- インストーラーは WebView2 Runtime を同梱・自動導入しません。不足時はアプリの公式入手先案内を使用してください。

## コメントモード

`PdfTextMap` はPdfPigのDocstrumによる行・ブロック検出と読み順推定を使用し、文字の四隅から表示矩形を作ります。文字間の空白と行の区切りを補い、キャレット・アンカー・選択範囲を `PdfCaretSelection` で管理します。日本語CJKの文字情報を読む試験を含み、OCR・PDF本文の書き換えは行いません。抽出はワーカースレッドで行います。回転・CropBoxによる表示座標から元のPDF既定ユーザー空間へ逆変換し、注釈の座標を保存します。

`PdfCommentCanvas` はページ描画を共用し、青い文字選択と緑のキャレットを重ねます。`PdfCommentEditor` はEnterで `PdfCommentDialog` を開き、空欄の追加を禁止します。Ctrl+Enterで追加、Enterで改行、Escで取り消しです。保存済みの注釈のContentsとQuadPoints／Rectを読み、コメントモードのクリックで本文を表示します。現在の版で既存注釈の変更・削除は行いません。

`PdfEditSession` はコメントもUndo/Redo・保存済み基準に含めます。`PdfComments` がISO 32000のHighlight／Popup注釈を出力し、`/QuadPoints` は既定ユーザー空間の四角形を反時計回りに格納します。`/Contents` はUnicode PDF文字列、改行はCRとし、`/T`、`/NM`、`/CreationDate`、`/M`、`/P`、Popupの親参照を設定します。標準の `/AP /N` Form XObjectを与え、Multiply・透明度を含めたハイライト表示を作ります。本文の画像化や別ファイルへのコメント保存は行いません。ページをまたぐコメントは各ページの注釈として保存し、追加全体を1回でUndoできます。

ページ操作とコメントは排他的なモードです。変更済みならモード切り替え時にも保存・破棄・取消を確認します。保存は既存の一時ファイル・Flush・再読込検証・原本SHA-256確認・原子的な置換を共用します。文字情報のないPDF、複雑な段組み、縦書き、特殊なフォントや読み順の完全対応は保証しません。PDF/A／PDF/UAの適合性を新たに保証するものではありません。

仕様: [ISO 32000注釈の関連箇所](https://pdf-issues.pdfa.org/32000-2-2020/clause12.html)、[PdfPig](https://github.com/UglyToad/PdfPig)。PdfPigのLICENSE・NOTICESを配布物へ含めます。

## 確認

`scripts/Test.ps1`: **70件**。既存のPDF編集・コピー試験に、複数PDFのインポート、拡大縮小、通常表示からの倍率引き継ぎ、モード名とCtrl+N削除の検査を含みます。原本PDFは再現可能に生成します。保存後のページ数・内容順・回転・基本注釈・しおり参照、失敗時の元ファイル保持、外部変更検出、署名欄・暗号化の拒否、文書管理の保存先変更を検査します。描画試験は画面外の非アクティブなフォームと専用ブラウザープロファイルを使い、閲覧時はWebView2、編集時はWindows標準PDF描画とWinFormsの画素を確認します。レジストリ試験は `HKCU\Software\SaruPDF.Tests\<ランダム GUID>` 以下だけを使い、終了後に削除します。実際のPDF関連付けは変更しません。WebView2 Runtimeと通常ユーザー権限でのテスト用レジストリ書き込みが必要です。

`scripts/Test-Installer.ps1` は生成したセットアップを作業フォルダー内の専用ディレクトリにサイレント導入・更新・削除し、候補登録や既定設定・ユーザーファイル保持を検証します。**これは現在のユーザーに一時的な SaruPDF 登録とスタートメニューを作る試験です。** 既存の SaruPDF 登録がある場合は上書きせず停止します。結果は検証記録を参照してください。

Windows GUI の実施結果と再現手順は [検証記録](verification.md) に記載します。

## 公式資料

- [WebView2 のローカルコンテンツと file URI](https://learn.microsoft.com/microsoft-edge/webview2/concepts/working-with-local-content)
- [WebView2 ユーザーデータフォルダー](https://learn.microsoft.com/microsoft-edge/webview2/concepts/user-data-folder)
- [WinForms WebView2 とキーイベント](https://learn.microsoft.com/dotnet/api/microsoft.web.webview2.winforms.webview2)
- [AllowSetForegroundWindow](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-allowsetforegroundwindow)
- [PipeOptions.CurrentUserOnly](https://learn.microsoft.com/dotnet/api/system.io.pipes.pipeoptions)
- [OpenWithProgids への登録](https://learn.microsoft.com/windows/win32/shell/how-to-include-an-application-on-the-open-with-dialog-box)
- [Windows の既定アプリ設定 URI](https://learn.microsoft.com/windows/apps/develop/launch/launch-settings#apps)
- [NSIS マニュアル](https://nsis.sourceforge.io/Docs/)
