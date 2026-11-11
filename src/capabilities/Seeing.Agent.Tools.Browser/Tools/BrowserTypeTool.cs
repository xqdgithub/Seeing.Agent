using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Tools;
using System.Text.Json;

namespace Seeing.Agent.Tools.Browser.Tools;

/// <summary>
/// <c>browser_type</c>：在输入框内输入文本（可先清空、可自动回车提交）。
/// </summary>
public sealed class BrowserTypeTool : BrowserToolBase
{
    /// <summary>创建工具。</summary>
    public BrowserTypeTool(ILogger<BrowserTypeTool> logger, IBrowserSessionHost host)
        : base(logger, host)
    {
    }

    /// <inheritdoc />
    public override string Id => "browser_type";

    /// <inheritdoc />
    public override string Description =>
        "在网页输入框内输入文字，可先清空已有内容并可选回车提交。\n\n" +
        "- ref 来自 `browser_snapshot`（如 e2）；也可用 selector 后备。\n" +
        "- clear 默认 true：先全选删除再输入；想追加内容设为 false。\n" +
        "- submit 默认 false：设为 true 等价于输入后按 Enter（适合搜索框）。\n" +
        "- 只点击按钮用 `browser_click`；输入后要按其它键用 `browser_press`。";

    /// <inheritdoc />
    public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            @ref = new { type = "string", description = "输入框引用（如 e2，由 browser_snapshot 获得）" },
            selector = new { type = "string", description = "CSS 选择器（ref 不可用时的后备方案）" },
            text = new { type = "string", description = "要输入的文字" },
            clear = new
            {
                type = "boolean",
                @default = true,
                description = "是否先清空输入框已有内容（默认 true）"
            },
            submit = new
            {
                type = "boolean",
                @default = false,
                description = "输入后是否按 Enter（默认 false）"
            },
            delayMs = new
            {
                type = "integer",
                minimum = 0,
                maximum = 500,
                description = "逐字输入延迟（毫秒，默认 0；某些页面需要慢速输入才触发联想）"
            }
        },
        required = new[] { "text" }
    });

    /// <inheritdoc />
    public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        if (!TryGetPage(context.SessionId, out var page, out var failure))
            return failure!;

        var text = BrowserJson.ReadString(arguments, "text");
        if (text is null)
            return Failure("缺少必需参数: text");

        var target = ReadTarget(arguments);
        if (!target.HasElement)
            return Failure("缺少元素定位：请提供 ref（由 browser_snapshot 获得）或 selector");
        if (target.HasCoordinates)
            return Failure("输入文字不支持坐标定位，请提供 ref 或 selector");

        var clear = BrowserJson.ReadBool(arguments, "clear") ?? true;
        var submit = BrowserJson.ReadBool(arguments, "submit") ?? false;
        var delayMs = Math.Clamp(BrowserJson.ReadInt(arguments, "delayMs") ?? 0, 0, 500);

        try
        {
            var message = await page.TypeAsync(target, text, clear, submit, delayMs, context.CancellationToken)
                .ConfigureAwait(false);

            return Success(
                message + BrowserClickTool.DescribePage(page),
                context.SessionId,
                new Dictionary<string, object>
                {
                    ["target"] = target.ToString(),
                    ["length"] = text.Length,
                    ["submitted"] = submit,
                    ["url"] = page.CurrentUrl
                });
        }
        catch (Exception ex)
        {
            return FailureFrom(ex, "输入文字");
        }
    }
}

