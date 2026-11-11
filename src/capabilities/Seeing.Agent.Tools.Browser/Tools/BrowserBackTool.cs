using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Tools;
using System.Text.Json;

namespace Seeing.Agent.Tools.Browser.Tools;

/// <summary>
/// <c>browser_back</c>：浏览器历史后退。
/// </summary>
public sealed class BrowserBackTool : BrowserToolBase
{
    /// <summary>创建工具。</summary>
    public BrowserBackTool(ILogger<BrowserBackTool> logger, IBrowserSessionHost host)
        : base(logger, host)
    {
    }

    /// <inheritdoc />
    public override string Id => "browser_back";

    /// <inheritdoc />
    public override string Description =>
        "浏览器历史后退一页。\n\n" +
        "- 后退后页面元素会变，之前的 ref 全部失效，需要重新 `browser_snapshot`。\n" +
        "- 没有上一页时明确返回失败，不会静默停在原页。\n" +
        "- 前进用 `browser_forward`；重新加载当前页用 `browser_open` 传入当前地址。";

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
            var result = await page.BackAsync(waitUntil, context.CancellationToken).ConfigureAwait(false);

            if (string.Equals(before, result.Url, StringComparison.Ordinal))
                return Failure("没有可后退的历史记录（当前已是第一页）");

            return Success($"已后退到：{result.Render()}", context.SessionId,
                new Dictionary<string, object>
                {
                    ["url"] = result.Url,
                    ["title"] = result.Title,
                    ["ref_epoch"] = page.RefEpoch
                });
        }
        catch (Exception ex)
        {
            return FailureFrom(ex, "后退");
        }
    }
}

