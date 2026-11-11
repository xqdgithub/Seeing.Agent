using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Tools;
using System.Text.Json;

namespace Seeing.Agent.Tools.Browser.Tools;

/// <summary>
/// <c>browser_close</c>：关闭该会话的浏览器页面并释放上下文。
/// </summary>
/// <remarks>
/// 主动关闭比等空闲回收好：它当场释放渲染进程内存，并让工作台画布立刻显示「未启动」。
/// </remarks>
public sealed class BrowserCloseTool : BrowserToolBase
{
    /// <summary>创建工具。</summary>
    public BrowserCloseTool(ILogger<BrowserCloseTool> logger, IBrowserSessionHost host)
        : base(logger, host)
    {
    }

    /// <inheritdoc />
    public override string Id => "browser_close";

    /// <inheritdoc />
    public override string Description =>
        "关闭当前会话的浏览器页面，释放内存与登录态（cookie 随上下文销毁）。\n\n" +
        "- 关闭后其它 browser_* 工具需要重新 `browser_open`。\n" +
        "- 只关本会话的页面，不影响其它会话或浏览器进程本身。\n" +
        "- 任务结束应主动调用（默认 10 分钟无操作也会自动回收）。";

    /// <inheritdoc />
    public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new Dictionary<string, object>()
    });

    /// <inheritdoc />
    public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        if (string.IsNullOrWhiteSpace(context.SessionId))
            return Failure("当前执行没有会话 id，无法定位浏览器页面");

        if (!Host.HasPage(context.SessionId))
            return Failure("该会话没有打开浏览器页面（无需关闭）");

        try
        {
            await Host.CloseAsync(context.SessionId, context.CancellationToken).ConfigureAwait(false);

            return Success("已关闭浏览器页面", context.SessionId, new Dictionary<string, object>
            {
                ["url"] = string.Empty,
                ["closed"] = true
            });
        }
        catch (Exception ex)
        {
            return FailureFrom(ex, "关闭浏览器");
        }
    }
}
