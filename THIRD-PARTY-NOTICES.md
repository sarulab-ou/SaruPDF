# サードパーティーコンポーネント

- Microsoft.Web.WebView2 **1.0.4314.35** — Microsoft のライセンスに従います。
  https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.4314.35
  https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.4314.35/License
- self-contained 配布には .NET / Windows Desktop Runtime が含まれます。
  配布フォルダーの `LICENSE.txt` / `THIRD-PARTY-NOTICES.txt` も参照してください。
  https://github.com/dotnet/runtime/blob/main/LICENSE.TXT
- Microsoft Edge WebView2 Runtime は別途必要です。配布 ZIP には含めません。
  https://developer.microsoft.com/microsoft-edge/webview2/
- Windows インストーラーは NSIS（Ubuntu 公式パッケージ 3.09-4ubuntu1）で作成します。
  NSIS の実行部分のライセンス: https://nsis.sourceforge.io/License
  ビルドツールは配布 ZIP に同梱しません。
