using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Tools;
using System.Text.Json;

namespace Seeing.Agent.Tools.Browser.Tools;

/// <summary>
/// <c>browser_snapshot</c>：输出页面可交互元素清单（<c>e1</c>、<c>e2</c>…）与标题/地址。
/// </summary>
/// <remarks>
/// 这是 agent 操作页面的<b>主入口</b>：先快照拿到 ref，再用 <c>browser_click</c>/<c>browser_type</c>
/// 按 ref 操作。相比「猜 CSS 选择器」，ref 由快照现场打标，几乎不会写错。
/// </remarks>
public sealed class BrowserSnapshotTool : BrowserToolBase
{
    /// <summary>创建工具。</summary>
    public BrowserSnapshotTool(ILogger<BrowserSnapshotTool> logger, IBrowserSessionHost host)
        : base(logger, host)
    {
    }

    /// <inheritdoc />
    public override string Id => "browser_snapshot";

    /// <inheritdoc />
    public override string Description =>
        "列出当前网页的可交互元素（编号 e1、e2…），用于后续按 ref 点击与输入。\n\n" +
        "- 返回：页面标题、地址、元素清单（ref 角色 \"名称\" (状态)）、可见文本摘要。\n" +
        "- ref 只在本次页面状态内有效；导航或页面刷新后必须重新快照。\n" +
        "- 只读，不改变页面；要看像素画面用 `browser_screenshot`。\n" +
        "- 需要执行脚本或读 DOM 细节用 `browser_eval`。";

    /// <inheritdoc />
    public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            maxElements = new
            {
                type = "integer",
                minimum = 1,
                maximum = BrowserSnapshotBuilder.MaxElementsLimit,
                description = $"最多列出多少个元素（默认 {BrowserSnapshotBuilder.DefaultMaxElements}，上限 {BrowserSnapshotBuilder.MaxElementsLimit}）"
            },
            includeText = new
            {
                type = "boolean",
                @default = true,
                description = "是否附带页面可见文本摘要（默认 true；页面很长时会占较多上下文）"
            }
        }
    });

    /// <inheritdoc />
    public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        if (!TryGetPage(context.SessionId, out var page, out var failure))
            return failure!;

        var maxElements = BrowserJson.ReadInt(arguments, "maxElements") ?? BrowserSnapshotBuilder.DefaultMaxElements;
        var includeText = BrowserJson.ReadBool(arguments, "includeText") ?? true;

        try
        {
            var snapshot = await page.SnapshotAsync(
                Math.Clamp(maxElements, 1, BrowserSnapshotBuilder.MaxElementsLimit),
                includeText,
                context.CancellationToken).ConfigureAwait(false);

            return Success(
                snapshot.Render(),
            context.SessionId,
                new Dictionary<string, object>
                {
                    ["url"] = snapshot.Url,
                    ["title"] = snapshot.Title,
                    ["element_count"] = snapshot.Elements.Count,
                    ["total_elements"] = snapshot.TotalElements,
                    ["truncated"] = snapshot.Truncated,
                    ["ref_epoch"] = snapshot.RefEpoch
                });
        }
        catch (Exception ex)
        {
            return FailureFrom(ex, "读取页面快照");
        }
    }
}
