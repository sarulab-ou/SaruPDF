using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace SaruPdf.Views;

internal sealed record PdfPageViewState(int Page, double Width);

// PDF zoom belongs to the PDF renderer, independently of WebView2.ZoomFactor.
// Scope the renderer session to this WebView's target, even with several PDFs open.
internal static class PdfViewportBridge
{
    internal static async Task<JsonElement?> EvaluateAsync(CoreWebView2 core, string expression)
    {
        using var info = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Target.getTargetInfo", "{}"));
        string? owner = info.RootElement.GetProperty("targetInfo").GetProperty("targetId").GetString();
        using var targets = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Target.getTargets", "{}"));
        foreach (var target in targets.RootElement.GetProperty("targetInfos").EnumerateArray())
        {
            if (!target.TryGetProperty("parentId", out var parent) || parent.GetString() != owner ||
                target.GetProperty("type").GetString() != "webview" ||
                !target.GetProperty("url").GetString()!.StartsWith("chrome-extension://mhjfbmdgcfjbbpaeojofohoefgiehjai/", StringComparison.Ordinal)) continue;
            using var attached = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Target.attachToTarget",
                JsonSerializer.Serialize(new { targetId = target.GetProperty("targetId").GetString(), flatten = true })));
            string session = attached.RootElement.GetProperty("sessionId").GetString()!;
            try
            {
                using var result = JsonDocument.Parse(await core.CallDevToolsProtocolMethodForSessionAsync(session, "Runtime.evaluate",
                    JsonSerializer.Serialize(new { expression, returnByValue = true })));
                if (result.RootElement.TryGetProperty("exceptionDetails", out _)) return null;
                return result.RootElement.GetProperty("result").TryGetProperty("value", out var value) ? value.Clone() : null;
            }
            finally
            {
                await core.CallDevToolsProtocolMethodAsync("Target.detachFromTarget", JsonSerializer.Serialize(new { sessionId = session }));
            }
        }
        return null;
    }
}
