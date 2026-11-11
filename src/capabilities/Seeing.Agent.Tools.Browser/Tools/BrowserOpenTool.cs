using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Interactions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Abstractions.Ui;
using Seeing.Agent.Abstractions.Ui.Workbench;
using System.Text.Json;

namespace Seeing.Agent.Tools.Browser.Tools;

/// <summary>
/// <c>browser_open</c>：打开网页（或导航已有页面到新地址），并在界面上打开「浏览器」工作台。
/// </summary>
/// <remarks>
/// <para>
/// 这是浏览器能力的入口：其它 <c>browser_*</c> 工具都要求该会话已有页面。
/// </para>
/// <para>
/// <b>为什么必须顺带打开工作台</b>：agent 调 <c>browser_open</c> 后，用户看到的是「什么都没发生」——
/// 页面在服务端真的建好了、后续工具也能操作，但界面上没有面板可看。
/// 用户得自己点「工作台 → 浏览器」才知道发生了什么，等于能力不可见。
/// </para>
/// <para>
/// <b>为什么是「尽力而为」</b>：无界面宿主（headless / Gateway / CLI）根本没有工作台扩展点，
/// 或用户压根没打开会话页面。这些情况下页面照建、工具照用，
/// 只是没有可视面板 —— 因此打开工作台失败不得改变工具本身的成功/失败。
/// </para>
/// </remarks>
public sealed class BrowserOpenTool : BrowserToolBase
{
    private readonly IOptionsMonitor<BrowserOptions> _options;

    /// <summary>创建工具。</summary>
    public BrowserOpenTool(
        ILogger<BrowserOpenTool> logger,
        IBrowserSessionHost host,
        IOptionsMonitor<BrowserOptions> options) : base(logger, host)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public override string Id => "browser_open";

    /// <inheritdoc />
    public override string Description =>
        "打开网页并在界面上显示浏览器面板，供后续 browser_* 工具操作。\n\n" +
        "- url 只支持 http、https、about:blank、data:text/html；内网与回环地址默认被拒。\n" +
        "- 会在用户界面打开「浏览器」工作台（无界面环境只建页面，不影响后续工具）。\n" +
        "- 已有页面时导航到新地址，保留同一浏览器会话（cookie 与登录态延续）。\n" +
        "- 打开后可调用 `browser_snapshot` 拿到可点击元素清单；需要看画面用 `browser_screenshot`。\n" +
        "- 关闭浏览器用 `browser_close`；只想抓文本内容用 `webfetch`（更快，无浏览器开销）。";

    /// <inheritdoc />
    public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            url = new
            {
                type = "string",
                description = "要打开的地址（http 或 https 开头；也可用 about:blank 清空页面）"
            },
            waitUntil = new
            {
                type = "string",
                @enum = new[] { "load", "domcontentloaded", "networkidle" },
                @default = "domcontentloaded",
                description = "等待页面就绪的时机：domcontentloaded（默认，通常够用）、load（含全部子资源）、networkidle（网络空闲）"
            },
            timeout = new
            {
                type = "integer",
                minimum = 1,
                maximum = 300,
                description = "导航超时（秒，可选；缺省用配置的 30 秒）"
            }
        },
        required = new[] { "url" }
    });

    /// <inheritdoc />
    public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        var url = BrowserJson.ReadString(arguments, "url");
        if (string.IsNullOrWhiteSpace(url))
            return Failure("缺少必需参数: url");

        var options = _options.CurrentValue;
        var verdict = BrowserUrlPolicy.Evaluate(url, options.AllowPrivateHosts);
        if (!verdict.Allowed)
            return Failure(verdict.Reason!);

        var waitText = BrowserJson.ReadString(arguments, "waitUntil");
        var waitUntil = BrowserJson.ReadEnum<BrowserWaitUntil>(arguments, "waitUntil") ?? BrowserWaitUntil.DomContentLoaded;
        if (waitText is { Length: > 0 } && BrowserJson.ReadEnum<BrowserWaitUntil>(arguments, "waitUntil") is null)
            return Failure($"waitUntil 只能是 {BrowserJson.Choices<BrowserWaitUntil>()}，收到 '{waitText}'");

        var timeoutSeconds = BrowserJson.ReadInt(arguments, "timeout");
        using var timeoutCts = timeoutSeconds is > 0
            ? new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds.Value, 1, 300)))
            : null;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            context.CancellationToken, timeoutCts?.Token ?? CancellationToken.None);

        try
        {
            // 新页面先建在 about:blank，再统一走一次带等待策略的导航：
            // 让「首次打开」与「已有页面再导航」走完全相同的代码路径（超时/等待语义一致）。
            var page = await Host.GetPageAsync(context.SessionId, null, linked.Token).ConfigureAwait(false);
            var result = await page.NavigateAsync(url.Trim(), waitUntil, linked.Token).ConfigureAwait(false);

            // 尽力打开工作台：失败不影响结果（无界面宿主 / 用户没开会话页面都属正常）。
            // 必须传**导航后的真实地址**：面板会立即导航到载荷里的 url，
            // 传 about:blank 会把刚打开的页面清空（实测缺陷）。
            var workbenchHint = await EnsureWorkbenchOpenAsync(context, result.Url).ConfigureAwait(false);

            return Success($"已打开：{result.Render()}（{result.ElapsedMs} 毫秒）{workbenchHint}", context.SessionId,
                new Dictionary<string, object>
                {
                    ["url"] = result.Url,
                    ["title"] = result.Title,
                    ["status"] = result.Status ?? 0,
                    ["ref_epoch"] = page.RefEpoch
                });
        }
        catch (Exception ex)
        {
            return FailureFrom(ex, "打开网页");
        }
    }

    /// <summary>
    /// 请求宿主打开「浏览器」工作台。
    /// </summary>
    /// <returns>给模型的补充说明（成功为空串，失败说明原因）。</returns>
    /// <remarks>
    /// 走 <see cref="WorkbenchRequestKinds.RegistryOpen"/> 而不是 <c>IWorkbenchClient</c>：
    /// 后者发的是「工作台自有动作」（<c>workbench.&lt;id&gt;.&lt;action&gt;</c>），
    /// 而「把工作台打开」属于注册表动作，由会话页的 dock 宿主处理。
    /// </remarks>
    /// <param name="context">工具上下文。</param>
    /// <param name="openedUrl">
    /// 刚导航到的地址。<b>必须传真实地址，不能写 <c>about:blank</c></b>：
    /// 面板拿到载荷后会立刻导航到它，写 about:blank 等于把 agent 刚打开的页面清空
    /// （实测过的缺陷——面板一挂载，页面就变成空白）。
    /// </param>
    private async Task<string> EnsureWorkbenchOpenAsync(ToolContext context, string? openedUrl)
    {
        if (context.Services is null)
            return string.Empty;

        // 无界面宿主：扩展点不存在，打开工作台没有意义
        if (context.Services.GetService(typeof(IUiPointCatalog)) is IUiPointCatalog points
            && points.Find(WorkbenchPoints.DockId) is null)
        {
            return string.Empty;
        }

        if (context.Services.GetService(typeof(IHostRequestManager)) is not IHostRequestManager manager)
            return string.Empty;

        var request = new HostRequest
        {
            SessionId = context.SessionId,
            CallId = context.CallId,
            Kind = WorkbenchRequestKinds.RegistryOpen,
            Payload = JsonSerializer.SerializeToElement(new
            {
                workbenchId = BrowserModule.IdValue,
                // 打开载荷带真实地址：面板重挂载时直接回到同一页。
                // 为空（理论上不会）时退回 about:blank，避免 null 让前端解析失败。
                openPayload = JsonSerializer.SerializeToElement(new
                {
                    url = string.IsNullOrWhiteSpace(openedUrl) ? "about:blank" : openedUrl
                })
            })
        };

        try
        {
            var ticket = await manager.BeginAsync(request, context.CancellationToken).ConfigureAwait(false);
            var response = await manager.WaitAsync(ticket, context.CancellationToken).ConfigureAwait(false);

            return response.Status switch
            {
                HostResponseStatus.Completed => string.Empty,
                HostResponseStatus.Unavailable => "（界面未就绪，工作台未能打开：请打开该会话页面后再试）",
                HostResponseStatus.Timeout => "（界面未在限定时间内响应，工作台未能打开）",
                HostResponseStatus.Cancelled => "（打开工作台已取消）",
                _ => $"（工作台未能打开：{response.Reason ?? "未知原因"}）"
            };
        }
        catch (OperationCanceledException)
        {
            // 工具整体被取消：不额外报告
            return string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "打开浏览器工作台失败（忽略：页面已可用）：{SessionId}", context.SessionId);
            return string.Empty;
        }
    }
}

