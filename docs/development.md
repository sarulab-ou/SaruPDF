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
artifacts/release/SaruPDF-0.3.0-win-x64/SaruPDF.exe
artifacts/release/SaruPDF-0.3.0-Setup-x64.exe
```

NuGet は `Microsoft.Web.WebView2 1.0.4314.35` と、MIT版の `PDFsharp 6.2.4` を完全固定し、`packages.lock.json` を保存しています。スクリプトは `--locked-mode` で復元します。SDK・パッケージキャッシュ・ビルド生成物・セットアップ EXE は Git 管理対象外です。依存を意図的に変更したときだけ `dotnet restore --force-evaluate` でロックを更新してください。PDFsharpとその依存ライセンスは配布フォルダーの `third-party-licenses/` に同梱します。

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
- `Documents/PdfEditSession.cs` / `Views/PdfEditor.cs`: 元のPDFのスナップショットとページ順・切り取り中の1ページ・Undo/Redoを管理し、編集用のページ一覧とプレビューを表示します。
- `WindowsIntegration/`: HKCU の ProgID、OpenWithProgids、Capabilities、RegisteredApplications の登録・所有者を確認した解除と、Windows 設定への遷移。
- `installer/SaruPDF.nsi`: ユーザー単位の導入、関連付け候補の任意登録、アンインストーラー。削除対象はビルド時に配布ファイルから生成し、インストール先の再帰削除は行いません。

追加プロセス側で相対パスを解決し、既存プロセスの PID に `AllowSetForegroundWindow` を呼んでから要求を送信します。終了中で受け付けられない場合や ACK が失われた場合は再試行し、旧プロセス終了後には自分が主プロセスになります。引き継ぎは最大約 20 秒でエラー表示します。最後の閉鎖から 200 ms の間に届いた要求は既存プロセスが処理できます。OS が前面化を認めない場合はタスクバーを点滅させます。常時最前面にはしません。

編集時は元ファイルをメモリーへ読み、ページ順だけを保持します。プレビューは専用の `%TEMP%/SaruPDF-edit-<GUID>/` に生成し、終了時に自分が作ったファイルだけを削除します。WebView2がファイルを保持する等の理由で削除できない場合は残ることがあります。ページ選択・数字＋Enterは編集一覧の選択とプレビューに反映され、切り取り対象は一覧で選んだページです。

出力はPDFsharpのModifyモードで開いた元のカタログを保持し、`PdfPages.MovePage`でページ参照を移します。新規文書への全ページインポートは行いません。切り取り中はページが一時的に一覧から外れますが保存は禁止し、貼り付けまたはUndoで戻せます。保存済みのページ順を別途保持するため、保存をまたぐUndo/Redoも未保存状態に反映します。

保存先と同じフォルダーに一時ファイルを書いてFlushし、ページ数を再読込で検証してから `File.Replace`（既存）または `File.Move`（新規）で置き換えます。上書き前には元ファイルのSHA-256を編集開始／直近保存時と比較します。失敗時は編集中の状態を保持し、一時ファイルを削除します。別名保存時は文書管理のパスも付け替え、別ウィンドウで開かれた保存先は拒否します。

## 対応範囲・制限

数字＋Enterによるページ移動は `Views/PageNumberInput.cs` と `PdfView.TryHandlePageKey` で処理します。WebView2 の `AllowHostInputProcessing` を有効にして通常の数字キーも受け取り、常設ツールバーを追加せず一時的な案内だけを表示します。テンキー、Backspace、Escに対応し、ウィンドウの非アクティブ化で入力を取り消します。修飾キー付きの入力はこの処理で消費しません。

組み込みPDFビューアーは読み込み後のフラグメントだけの変更をページ移動として扱わないため、移動ごとに一意なクエリーを付けた同じファイルURIへナビゲートし、`toolbar=0&page=<番号>` を指定します。再表示時のズームなどはWebView2に依存します。総ページ数を解析しての上限検査は行いません。

- 同一性は `Path.GetFullPath` と大文字小文字を無視する完全パス比較です。`.` / `..` / 相対パス / 大文字小文字の差を吸収し、別フォルダーの同名 PDF は別扱いです。
- シンボリックリンク・ハードリンク・ジャンクション・8.3 短縮名・ドライブ文字と UNC 名の別名を跨ぐ同一判定は未対応です。ファイル ID はキャッシュしません。外部アプリが同じパスのファイルを置換しても、そのパスの既存ウィンドウを優先します。最新版を表示するには一度閉じて再度開いてください。大文字小文字を区別する特殊なディレクトリは対象外です。
- ページ操作は同じPDF内の1ページずつの切り取り・貼り付けによる並べ替えに対応します。PDF本文の編集・ページ削除・別PDFとの結合・独自注釈UIは未実装です。暗号化PDFと署名欄のあるPDFは編集を拒否します。PDF/A・PDF/UA、特殊フォーム、ページラベルなどの保持は未検証です。WebView2が提供するUIはRuntimeの版に依存します。
- 文書の別 PDF への通常のリンクは共通の「開く」処理へ集約します。文書の置換を防ぐため外部ドラッグ＆ドロップを無効にし、同じ文書以外の内部ナビゲーションをキャンセルします。HTTP(S) 以外の外部プロトコル、非ユーザー操作の新規ウィンドウは許可しません。PDF 埋め込み添付ファイルや特殊なアクションは保証しません。
- 最初の 1 KiB に PDF ヘッダーがあるかを確認します。独自の PDF 構文解析は行わず、破損・暗号化・未対応機能は WebView2 の表示に依存します。ナビゲーション失敗はアプリ内に案内しますが、Runtime が「読み込み成功」として独自の PDF エラー画面を返す場合は、その画面で確認してください。
- 通常権限での使用を前提とします。同じユーザーでも管理者権限と通常権限を混在させるとパイプ接続が拒否され、引き継ぎエラーになります。
- EXE／インストーラーへの署名と自動更新は未対応です。Windows 10 全ビルド・別 PC での互換性は未確認です。
- インストーラーは WebView2 Runtime を同梱・自動導入しません。不足時はアプリの公式入手先案内を使用してください。

## 確認

`scripts/Test.ps1`: **43 件成功**。既存31件に、編集の10件と編集UI・ウィンドウコマンドの描画試験2件を追加しました。原本PDFは再現可能に生成します。保存後のページ数・内容順・回転・基本注釈・しおり参照、失敗時の元ファイル保持、外部変更検出、署名欄・暗号化の拒否、文書管理の保存先変更を検査します。描画試験は画面外の非アクティブなフォームと専用ブラウザープロファイルを使い、実際のWebView2の画素を確認します。レジストリ試験は `HKCU\Software\SaruPDF.Tests\<ランダム GUID>` 以下だけを使い、終了後に削除します。実際のPDF関連付けは変更しません。WebView2 Runtimeと通常ユーザー権限でのテスト用レジストリ書き込みが必要です。

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
