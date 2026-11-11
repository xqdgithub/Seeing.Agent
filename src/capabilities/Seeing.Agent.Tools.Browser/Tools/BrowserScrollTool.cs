using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Tools;
using System.Text.Json;

namespace Seeing.Agent.Tools.Browser.Tools;

/// <summary>
/// <c>browser_scroll</c>：滚动页面或指定元素内部。
/// </summary>
public sealed class BrowserScrollTool : BrowserToolBase
{
    /// <summary>创建工具。</summary>
    public BrowserScrollTool(ILogger<BrowserScrollTool> logger, IBrowserSessionHost host)
        : base(logger, host)
    {
    }

    /// <inheritdoc />
    public override string Id => "browser_scroll";

    /// <inheritdoc />
    public override string Description =>
        "滚动网页：默认滚动整页，给 ref 时滚动该元素内部。\n\n" +
        "- direction 默认 down；amount 为像素（默认 600）。\n" +
        "- 滚动后页面可能加载新内容，需要新的 ref 就重新 `browser_snapshot`。\n" +
        "- 滚动只为看到内容时，优先考虑用 `browser_snapshot` 的可见文本，省去多轮操作。";

    /// <inheritdoc />
    public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            direction = new
            {
                type = "string",
                @enum = new[] { "up", "down", "left", "right" },
                @default = "down",
                description = "滚动方向：up、down（默认）、left、right"
            },
            amount = new
            {
                type = "integer",
                minimum = 1,
                maximum = 20000,
                description = "滚动距离（像素，默认 600）"
            },
            @ref = new
            {
                type = "string",
                description = "在该元素内部滚动（可选；缺省时滚动整页）"
            }
        }
    });

    /// <inheritdoc />
    public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        if (!TryGetPage(context.SessionId, out var page, out var failure))
            return failure!;

        var directionText = BrowserJson.ReadString(arguments, "direction");
        var direction = BrowserJson.ReadEnum<BrowserScrollDirection>(arguments, "direction")
                        ?? BrowserScrollDirection.Down;
        if (directionText is { Length: > 0 } && BrowserJson.ReadEnum<BrowserScrollDirection>(arguments, "direction") is null)
            return Failure($"direction 只能是 {BrowserJson.Choices<BrowserScrollDirection>()}，收到 '{directionText}'");

        var amount = BrowserJson.ReadInt(arguments, "amount") ?? 600;

        var reference = BrowserJson.ReadString(arguments, "ref");
        BrowserElementTarget? target = string.IsNullOrWhiteSpace(reference)
            ? null
            : new BrowserElementTarget { Ref = reference };

        try
        {
            var message = await page.ScrollAsync(direction, amount, target, context.CancellationToken)
                .ConfigureAwait(false);

            return Success(
                message,
            context.SessionId,
                new Dictionary<string, object>
                {
                    ["direction"] = direction.ToString(),
                    ["amount"] = amount,
                    ["target"] = target?.ToString() ?? "page"
                });
        }
        catch (Exception ex)
        {
            return FailureFrom(ex, "滚动页面");
        }
    }
}
