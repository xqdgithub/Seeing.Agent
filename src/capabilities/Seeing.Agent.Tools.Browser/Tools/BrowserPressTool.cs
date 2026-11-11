using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Tools;
using System.Text.Json;

namespace Seeing.Agent.Tools.Browser.Tools;

/// <summary>
/// <c>browser_press</c>：向当前聚焦元素发送按键（Enter、Tab、Escape、方向键、组合键）。
/// </summary>
public sealed class BrowserPressTool : BrowserToolBase
{
    /// <summary>创建工具。</summary>
    public BrowserPressTool(ILogger<BrowserPressTool> logger, IBrowserSessionHost host)
        : base(logger, host)
    {
    }

    /// <inheritdoc />
    public override string Id => "browser_press";

    /// <inheritdoc />
    public override string Description =>
        "向当前聚焦元素发送按键，如 Enter、Tab、Escape、ArrowDown 与组合键 Control+A。\n\n" +
        "- 键名用标准名称：Enter、Tab、Escape、Backspace、Delete、ArrowUp/Down/Left/Right、PageUp/PageDown、Home/End、F1–F12、字母数字。\n" +
        "- 组合键写成 Modifier+Key，如 Control+A、Shift+Tab、Control+Shift+I。\n" +
        "- 先聚焦目标元素：用 `browser_click` 点一下，或 `browser_type` 输入后自然聚焦。\n" +
        "- 单纯输入文字用 `browser_type`（逐字输入，不是按键）。";

    /// <inheritdoc />
    public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            key = new
            {
                type = "string",
                description = "按键名或组合键，如 Enter、Tab、Escape、ArrowDown、Control+A、Shift+Tab"
            }
        },
        required = new[] { "key" }
    });

    /// <inheritdoc />
    public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        if (!TryGetPage(context.SessionId, out var page, out var failure))
            return failure!;

        var key = BrowserJson.ReadString(arguments, "key");
        if (string.IsNullOrWhiteSpace(key))
            return Failure("缺少必需参数: key");

        try
        {
            var message = await page.PressKeyAsync(key.Trim(), context.CancellationToken).ConfigureAwait(false);

            return Success(
                message + BrowserClickTool.DescribePage(page),
                context.SessionId,
                new Dictionary<string, object>
                {
                    ["key"] = key.Trim(),
                    ["url"] = page.CurrentUrl
                });
        }
        catch (Exception ex)
        {
            return FailureFrom(ex, $"按键 {key}");
        }
    }
}

