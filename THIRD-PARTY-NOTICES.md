# サードパーティーコンポーネント

- Microsoft.Web.WebView2 **1.0.4314.35** — Microsoft のライセンスに従います。
  https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.4314.35
  https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.4314.35/License
- PDFsharp **6.2.4** — MIT License。ページ順の編集・保存に使用します。
  https://www.nuget.org/packages/PDFsharp/6.2.4
  ライセンス原文はソースの `assets/licenses/PDFsharp-LICENSE.txt`、配布物の `third-party-licenses/PDFsharp-LICENSE.txt` に同梱します。
- Microsoft.Extensions.Logging.Abstractions **8.0.3** / Microsoft.Extensions.DependencyInjection.Abstractions **8.0.2** — MIT License。PDFsharpの依存です。配布物の `third-party-licenses/` に各パッケージのライセンスと通知を同梱します。
- self-contained 配布には .NET / Windows Desktop Runtime が含まれます。
  配布フォルダーの `LICENSE.txt` / `THIRD-PARTY-NOTICES.txt` も参照してください。
  https://github.com/dotnet/runtime/blob/main/LICENSE.TXT
- Microsoft Edge WebView2 Runtime は別途必要です。配布物には含めません。
  https://developer.microsoft.com/microsoft-edge/webview2/
- Windows インストーラーは NSIS（Ubuntu 公式パッケージ 3.09-4ubuntu1）で作成します。
  NSIS の実行部分のライセンス: https://nsis.sourceforge.io/License
  ビルドツールは配布物に同梱しません。
