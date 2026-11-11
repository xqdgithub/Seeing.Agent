using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Tools;
using System.Text.Json;

namespace Seeing.Agent.Tools.Browser.Tools;

/// <summary>
/// <c>browser_forward</c>：浏览器历史前进。
/// </summary>
public sealed class BrowserForwardTool : BrowserToolBase
{
    /// <summary>创建工具。</summary>
    public BrowserForwardTool(ILogger<BrowserForwardTool> logger, IBrowserSessionHost host)
        : base(logger, host)
    {
    }

    /// <inheritdoc />
    public override string Id => "browser_forward";

    /// <inheritdoc />
    public override string Description =>
        "浏览器历史前进一页（与 `browser_back` 相反）。\n\n" +
        "- 前进后页面元素会变，之前的 ref 全部失效，需要重新 `browser_snapshot`。\n" +
        "- 没有下一页时明确返回失败，不会静默停在原页。";

    /// <inheritdoc />
    public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            waitUntil = new
            {
                type = "string",
                @enum = new[] { "load", "domcontentloaded", "networkidle" },
                @default = "domcontentloaded",
                description = "等待页面就绪的时机：load、domcontentloaded（默认）、networkidle"
            }
        }
    });

    /// <inheritdoc />
    public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        if (!TryGetPage(context.SessionId, out var page, out var failure))
            return failure!;

        var waitText = BrowserJson.ReadString(arguments, "waitUntil");
        var waitUntil = BrowserJson.ReadEnum<BrowserWaitUntil>(arguments, "waitUntil") ?? BrowserWaitUntil.DomContentLoaded;
        if (waitText is { Length: > 0 } && BrowserJson.ReadEnum<BrowserWaitUntil>(arguments, "waitUntil") is null)
            return Failure($"waitUntil 只能是 {BrowserJson.Choices<BrowserWaitUntil>()}，收到 '{waitText}'");

        try
        {
            var before = page.CurrentUrl;
            var result = await page.ForwardAsync(waitUntil, context.CancellationToken).ConfigureAwait(false);

            if (string.Equals(before, result.Url, StringComparison.Ordinal))
                return Failure("没有可前进的历史记录（当前已是最新页）");

            return Success($"已前进到：{result.Render()}", context.SessionId,
                new Dictionary<string, object>
                {
                    ["url"] = result.Url,
                    ["title"] = result.Title,
                    ["ref_epoch"] = page.RefEpoch
                });
        }
        catch (Exception ex)
        {
            return FailureFrom(ex, "前进");
        }
    }
}

