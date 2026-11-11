using Microsoft.Extensions.Logging;
using PuppeteerSharp.Input;
using Seeing.Agent.Abstractions.Tools;
using System.Text.Json;

namespace Seeing.Agent.Tools.Browser.Tools;

/// <summary>
/// <c>browser_click</c>：按 ref（推荐）、CSS 选择器或视口坐标点击。
/// </summary>
public sealed class BrowserClickTool : BrowserToolBase
{
    /// <summary>创建工具。</summary>
    public BrowserClickTool(ILogger<BrowserClickTool> logger, IBrowserSessionHost host)
        : base(logger, host)
    {
    }

    /// <inheritdoc />
    public override string Id => "browser_click";

    /// <inheritdoc />
    public override string Description =>
        "点击网页元素：按 ref（推荐）、CSS 选择器或视口坐标。\n\n" +
        "- ref 来自 `browser_snapshot`（如 e3）；导航或刷新后失效，需重新快照。\n" +
        "- ref/selector 与 x+y 坐标只能二选一；坐标单位为 CSS 像素（与视口同尺度）。\n" +
        "- 点击后页面可能变化：下一步通常要重新 `browser_snapshot` 拿新 ref。\n" +
        "- 输入文字用 `browser_type`；按键用 `browser_press`。";

    /// <inheritdoc />
    public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            @ref = new { type = "string", description = "元素引用（如 e3，由 browser_snapshot 获得）" },
            selector = new { type = "string", description = "CSS 选择器（ref 不可用时的后备方案）" },
            x = new { type = "number", description = "视口 X 坐标（CSS 像素，与 y 同时提供）" },
            y = new { type = "number", description = "视口 Y 坐标（CSS 像素，与 x 同时提供）" },
            button = new
            {
                type = "string",
                @enum = new[] { "left", "right", "middle" },
                @default = "left",
                description = "鼠标键：left（默认）、right、middle"
            },
            clickCount = new
            {
                type = "integer",
                minimum = 1,
                maximum = 3,
                description = "连击次数（默认 1；2 为双击）"
            }        }
    });

    /// <inheritdoc />
    public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        if (!TryGetPage(context.SessionId, out var page, out var failure))
            return failure!;

        var target = ReadTarget(arguments);
        if (target.Validate() is { } problem)
            return Failure(problem);

        var buttonText = BrowserJson.ReadString(arguments, "button");
        var button = BrowserJson.ReadEnum<MouseButton>(arguments, "button") ?? MouseButton.Left;
        if (buttonText is { Length: > 0 } && BrowserJson.ReadEnum<MouseButton>(arguments, "button") is null)
            return Failure($"button 只能是 left、right、middle，收到 '{buttonText}'");

        var clickCount = BrowserJson.ReadInt(arguments, "clickCount") ?? 1;

        try
        {
            var message = await page.ClickAsync(target, button, clickCount, context.CancellationToken)
                .ConfigureAwait(false);

            return Success($"{message}{DescribePage(page)}", context.SessionId,
                new Dictionary<string, object>
                {
                    ["target"] = target.ToString(),
                    ["url"] = page.CurrentUrl,
                    ["ref_epoch"] = page.RefEpoch
                });
        }
        catch (Exception ex)
        {
            return FailureFrom(ex, "点击元素");
        }
    }

    /// <summary>点击后提示「现在在哪个页面」（导航型点击很常见，模型需要知道地址变了）。</summary>
    internal static string DescribePage(BrowserPageHandle page)
    {
        var title = string.IsNullOrWhiteSpace(page.CurrentTitle) ? "(无标题)" : page.CurrentTitle;
        return $"\n当前页面：{title}（{page.CurrentUrl}）";
    }
}

