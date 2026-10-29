# SaruPDF 0.2.0

Windows 10 / 11（x64）用のシンプルな PDF Viewer です。C# / .NET 10 / Windows Forms と Microsoft Edge WebView2 の標準 PDF Viewer を使います。

## ダウンロード

**[Windows x64 インストーラーをダウンロード](https://github.com/sarulab-ou/SaruPDF/releases/download/v0.2.0/SaruPDF-0.2.0-Setup-x64.exe)**

[最新リリースと変更内容](https://github.com/sarulab-ou/SaruPDF/releases/latest) · [SHA256](https://github.com/sarulab-ou/SaruPDF/releases/download/v0.2.0/SaruPDF-0.2.0-SHA256SUMS.txt)

リポジトリが非公開の場合、ダウンロードには GitHub へのログインとリポジトリへのアクセス権が必要です。

## インストーラーと PDF の関連付け

`artifacts/release/SaruPDF-0.2.0-Setup-x64.exe` を起動し、画面に従ってインストールしてください。既定の保存先は `%LOCALAPPDATA%\Programs\SaruPDF`、現在のユーザー専用で管理者権限は不要です。更新時は、SaruPDF の全ウィンドウを閉じてください。

- 「PDF を開くアプリの候補に登録」を選ぶと、現在のユーザーの「プログラムから開く」と Windows の既定アプリ候補に **SaruPDF** を登録します。初期状態では選択されています。
- 完了画面の「PDF の関連付け設定を開く」、スタートメニューの「SaruPDF → PDF の関連付け」、またはアプリの「設定 → PDF の関連付け」から、Windows の既定アプリ設定を開けます。そこで `.pdf` のアプリとして SaruPDF を選択すると、PDF のダブルクリックで起動できます。
- デスクトップのショートカットは任意選択です。スタートメニューと Windows のインストール済みアプリには自動登録します。
- アンインストールは Windows の「インストールされているアプリ」から、またはインストール先の `Uninstall.exe` で行います。自分が登録した PDF 候補を削除します。別の場所の新しい SaruPDF が登録し直していた場合は、その登録を保持します。
- アンインストールでは配布したファイルだけを削除します。利用者が後から置いたファイルや、WebView2 のキャッシュは保持します。

Windows 10/11 の既定アプリは、Windows の選択画面で利用者が確定します。インストーラーは既存の `.pdf` の既定値や `UserChoice` を書き換えません。アンインストール前に SaruPDF を既定にしていた場合は、Windows の画面で次の PDF アプリを選んでください。

## 起動

スタートメニューの「SaruPDF」、またはインストール先の `SaruPDF.exe` を起動します。コマンドで PDF を開くこともできます。以下はインストール先をカレントディレクトリにした場合の例です。

```powershell
.\SaruPDF.exe
.\SaruPDF.exe "C:\Documents\論文 A.pdf"
.\SaruPDF.exe "C:\Documents\論文 A.pdf" "C:\Documents\論文 B.pdf"
```

- 「PDF を開く」ボタン／ファイルメニュー／`Ctrl+O` で選択します。複数選択できます。
- 最初の空ウィンドウは、そのまま最初の PDF の表示に使います。
- PDF ごとに独立したウィンドウを表示します。表示中に別の PDF を開くと新しいウィンドウになります。移動・最小化・最大化・タスクバー・Alt+Tab の操作は通常の Windows アプリと同じです。
- 同じ PDF を再度開くと既存ウィンドウに移ります。ページやズームを変えるための再読み込みは行いません。最小化中なら復元します。
- 引数なしで追加起動すると、最後にアクティブだったウィンドウに移ります。
- `Ctrl+W` は現在のウィンドウだけを閉じます。最後のウィンドウを閉じるとアプリプロセスも終了します。
- ページ移動・検索・ズーム・印刷・保存は WebView2 標準 PDF ツールバーを使います。パスはウィンドウ下端で確認・選択できます。

## 実行時の依存関係

インストーラーには **self-contained / win-x64** のアプリと .NET / Windows Desktop Runtime を同梱します。.NET の追加インストールは不要です。

**WebView2 Runtime は .NET とは別です。** PC に Evergreen WebView2 Runtime が必要です。不足している場合はアプリ内に案内を表示します。[Microsoft 公式ダウンロード](https://developer.microsoft.com/microsoft-edge/webview2/#download-section)から Evergreen Runtime（x64）をインストールし、SaruPDF の全ウィンドウを閉じて再起動してください。

PDF 本文はローカルの `file:` URI で表示し、SaruPDF が文書を外部サービスにアップロードする処理はありません。WebView2 自身の更新・ネットワーク動作は Microsoft の製品仕様に従います。文書からクリックした HTTP(S) リンクは既定のブラウザーに渡します。

WebView2 のキャッシュ等は、書き込み可能な `%LOCALAPPDATA%\SaruPDF\WebView2\Session-<ログオンセッション番号>` に保存します。実行ファイルの置き場所には保存しません。

## Explorer の「プログラムから開く」

1. インストーラーで SaruPDF をインストールします。
2. PDF を右クリックし、「プログラムから開く」→「別のプログラムを選択」を選びます。
3. SaruPDF を選択するか、「PC でアプリを選択」等からインストール先の `SaruPDF.exe` を指定します（名称は Windows の版で異なります）。

既定にする場合は、Windows の「設定」→「アプリ」→「既定のアプリ」で `.pdf` のアプリを選択するか、上記の選択画面で「常に使う」を選択します。アプリの「設定 → PDF の関連付け」からも候補登録と Windows 設定への移動ができます。

候補登録だけを行うコマンドもあります。これらは実行時の現在のユーザーに作用し、PDF ウィンドウは開きません。通常起動やビルドのみでは登録しません。

```powershell
.\SaruPDF.exe --register-pdf
.\SaruPDF.exe --unregister-pdf
.\SaruPDF.exe --pdf-association
```

## 開発・ビルド

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

`Build.ps1` は Release ビルド、`Test.ps1` は自作のコンソール型テストランナー、`Publish.ps1` は インストーラーの入力となる self-contained フォルダーを作成します。PowerShell の実行ポリシーでスクリプトが禁止されている環境では、組織のポリシーに従って実行してください。

出力先を変える場合は `scripts/Publish.ps1 -OutputRoot artifacts/release` のように指定できます。`Build-Installer.ps1` は既定でこの場所に publish と NSIS コンパイルを行います。既存の配布物を使う場合は `-SkipPublish` を付けます。

インストーラーのビルドには WSL Ubuntu 24.04 と NSIS が必要です。`Install-InstallerTools.ps1` は Ubuntu 公式の **NSIS 3.09-4ubuntu1** と `nsis-common` を `.tools/nsis-root` に展開します。`apt install`、管理者権限での Linux パッケージ変更、Windows の NSIS インストールは行いません。初回は Ubuntu 公式リポジトリへの接続が必要です。

GitHub で配布する場合は、ソースをコミット・push した後、`scripts/Publish-GitHubRelease.ps1` を実行します。WSL の GitHub CLI 認証が必要です。スクリプトは既存リリースの上書き、リポジトリの公開範囲の変更、コミットを自動で行いません。

```text
artifacts/release/SaruPDF-0.2.0-win-x64/SaruPDF.exe
artifacts/release/SaruPDF-0.2.0-Setup-x64.exe
```

NuGet は安定版 `Microsoft.Web.WebView2 1.0.4314.35` を完全固定し、`packages.lock.json` を保存しています。スクリプトは `--locked-mode` で復元します。SDK・パッケージキャッシュ・ビルド生成物・セットアップ EXE は Git 管理対象外です。依存を意図的に変更したときだけ `dotnet restore --force-evaluate` でロックを更新してください。

## 構成と多重起動

- `Documents/DocumentPath.cs`: パス正規化、読み取り・拡張子・PDF ヘッダーの確認、`System.Uri` による URI 作成。
- `Documents/WindowCatalog.cs`: UI スレッドが所有する文書とウィンドウの対応表。
- `Instances/`: ユーザー／ログオンセッション単位の名前付き Mutex と同一ユーザー限定の名前付きパイプ。長さ付き UTF-8 JSON、最大 1 MiB・256 パス、タイムアウト付きです。
- `PdfApplicationContext.cs`: メインウィンドウを固定しないアプリ寿命管理。パイプ要求を UI スレッドへ渡し、ウィンドウ登録後に ACK を返します。同じ要求 ID の再送は直近 128 件を除重します。
- `Views/`: 通常の Windows Forms ウィンドウと非同期 WebView2 表示。初期化／読み込み完了からの前面化処理はありません。
- `WindowsIntegration/`: HKCU の ProgID、OpenWithProgids、Capabilities、RegisteredApplications の登録・所有者を確認した解除と、Windows 設定への遷移。
- `installer/SaruPDF.nsi`: ユーザー単位の導入、関連付け候補の任意登録、アンインストーラー。削除対象はビルド時に配布ファイルから生成し、インストール先の再帰削除は行いません。

追加プロセス側で相対パスを解決し、既存プロセスの PID に `AllowSetForegroundWindow` を呼んでから要求を送信します。終了中で受け付けられない場合や ACK が失われた場合は再試行し、旧プロセス終了後には自分が主プロセスになります。引き継ぎは最大約 20 秒でエラー表示します。最後の閉鎖から 200 ms の間に届いた要求は既存プロセスが処理できます。OS が前面化を認めない場合はタスクバーを点滅させます。常時最前面にはしません。

## 対応範囲・制限

- 同一性は `Path.GetFullPath` と大文字小文字を無視する完全パス比較です。`.` / `..` / 相対パス / 大文字小文字の差を吸収し、別フォルダーの同名 PDF は別扱いです。
- シンボリックリンク・ハードリンク・ジャンクション・8.3 短縮名・ドライブ文字と UNC 名の別名を跨ぐ同一判定は未対応です。ファイル ID はキャッシュしません。外部アプリが同じパスのファイルを置換しても、そのパスの既存ウィンドウを優先します。最新版を表示するには一度閉じて再度開いてください。大文字小文字を区別する特殊なディレクトリは対象外です。
- PDF 本文の編集・ページ操作・独自注釈 UI は未実装です。WebView2 が提供する UI は Runtime の版に依存します。
- 文書の別 PDF への通常のリンクは共通の「開く」処理へ集約します。文書の置換を防ぐため外部ドラッグ＆ドロップを無効にし、同じ文書以外の内部ナビゲーションをキャンセルします。HTTP(S) 以外の外部プロトコル、非ユーザー操作の新規ウィンドウは許可しません。PDF 埋め込み添付ファイルや特殊なアクションは保証しません。
- 最初の 1 KiB に PDF ヘッダーがあるかを確認します。独自の PDF 構文解析は行わず、破損・暗号化・未対応機能は WebView2 の表示に依存します。ナビゲーション失敗はアプリ内に案内しますが、Runtime が「読み込み成功」として独自の PDF エラー画面を返す場合は、その画面で確認してください。
- 通常権限での使用を前提とします。同じユーザーでも管理者権限と通常権限を混在させるとパイプ接続が拒否され、引き継ぎエラーになります。
- EXE／インストーラーへの署名と自動更新は未対応です。Windows 10 全ビルド・別 PC での互換性は未確認です。
- インストーラーは WebView2 Runtime を同梱・自動導入しません。不足時はアプリの公式入手先案内を使用してください。

## 確認

`scripts/Test.ps1`: **22 件成功**。既存の文書・ウィンドウ・通信の 15 件に、関連付けの引用符、候補登録、既定値保持、解除、他コピーの登録保持、繰り返し実行、CLI／設定 URI の検査を追加しました。レジストリ試験は `HKCU\Software\SaruPDF.Tests\<ランダム GUID>` 以下だけを使い、終了後に削除します。実際の PDF 関連付けは変更しません。

`scripts/Test-Installer.ps1` は生成したセットアップを作業フォルダー内の専用ディレクトリにサイレント導入・更新・削除し、候補登録や既定設定・ユーザーファイル保持を検証します。**これは現在のユーザーに一時的な SaruPDF 登録とスタートメニューを作る試験です。** 既存の SaruPDF 登録がある場合は上書きせず停止します。結果は検証記録を参照してください。

Windows GUI の実施結果と再現手順は [docs/verification.md](docs/verification.md) に記載します。

## 公式資料

- [WebView2 のローカルコンテンツと file URI](https://learn.microsoft.com/microsoft-edge/webview2/concepts/working-with-local-content)
- [WebView2 ユーザーデータフォルダー](https://learn.microsoft.com/microsoft-edge/webview2/concepts/user-data-folder)
- [WinForms WebView2 とキーイベント](https://learn.microsoft.com/dotnet/api/microsoft.web.webview2.winforms.webview2)
- [AllowSetForegroundWindow](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-allowsetforegroundwindow)
- [PipeOptions.CurrentUserOnly](https://learn.microsoft.com/dotnet/api/system.io.pipes.pipeoptions)
- [OpenWithProgids への登録](https://learn.microsoft.com/windows/win32/shell/how-to-include-an-application-on-the-open-with-dialog-box)
- [Windows の既定アプリ設定 URI](https://learn.microsoft.com/windows/apps/develop/launch/launch-settings#apps)
- [NSIS マニュアル](https://nsis.sourceforge.io/Docs/)
