using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Tools.Support;

namespace Seeing.Agent.Tools.Browser.Tools;

/// <summary>
/// 浏览器工具基类：把「取会话页面 → 执行 → 异常归一化」这段重复逻辑收在一处。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么每个动作一个工具而不是一个 <c>browser</c> 工具 + action 枚举</b>：
/// 权限判定的键是 <c>(PermissionKind.Tool, toolName)</c>，<b>不含参数</b>。合成一个工具后，
/// 用户无法「允许截图、禁止执行任意 JS」——审批只能整包放行。拆开后 <c>browser_eval</c>
/// 可以单独设为必须审批。
/// </para>
/// <para>
/// <b>失败文案为什么这么具体</b>：模型看不到界面，只知道工具返回了什么。含糊的「操作失败」
/// 会让它反复重试同一件事。每条失败都说明「为什么 + 下一步做什么」。
/// </para>
/// </remarks>
public abstract class BrowserToolBase : BuiltInToolBase
{
    /// <summary>创建工具。</summary>
    /// <param name="logger">日志。</param>
    /// <param name="host">会话页面宿主。</param>
    protected BrowserToolBase(ILogger logger, IBrowserSessionHost host) : base(logger)
    {
        Host = host ?? throw new ArgumentNullException(nameof(host));
    }

    /// <summary>会话页面宿主。</summary>
    protected IBrowserSessionHost Host { get; }

    /// <inheritdoc />
    public override ToolCategory Category => ToolCategory.Network;

    /// <summary>
    /// 取该会话已打开的页面；没有则返回带指引的失败结果。
    /// </summary>
    /// <remarks>
    /// <b>为什么必须显式传 sessionId 而不是记在字段上</b>：工具是 DI 单例
    /// （<c>BrowserModule.ConfigureServices</c> 里 <c>TryAddSingleton</c>），
    /// 多个会话会并发调用同一个实例。把 sessionId 存进实例字段会让后到的调用
    /// 覆盖先到的——症状是 A 会话的工具结果里附带了 B 会话的弹窗文本（跨会话信息泄漏），
    /// 且这种竞态不可复现、极难排查。
    /// </remarks>
    protected bool TryGetPage(string sessionId, out BrowserPageHandle page, out ToolResult? failure)
    {
        page = null!;
        failure = null;

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            failure = Failure("当前执行没有会话 id，无法定位浏览器页面");
            return false;
        }

        if (Host.TryGetPage(sessionId) is not { } existing)
        {
            failure = Failure("该会话还没有打开页面，请先调用 `browser_open` 打开一个网址");
            return false;
        }

        page = existing;
        return true;
    }

    /// <summary>把浏览器层异常翻译成可读失败结果。</summary>
    protected ToolResult FailureFrom(Exception exception, string action)
    {
        var suffix = SessionDialogSuffix();
        return exception switch
        {
            BrowserElementNotFoundException notFound => Failure(notFound.Message),
            BrowserElementNotInteractableException notInteractable => Failure(notInteractable.Message),
            BrowserElementValidationException invalid => Failure(invalid.Message),
            BrowserUnavailableException unavailable => Failure(unavailable.Message),
            TimeoutException => Failure($"{action}超时：页面在限定时间内没有响应"),
            OperationCanceledException => Failure($"{action}已取消"),
            _ when IsNavigationFailure(exception) => Failure($"{action}失败：{exception.Message}（地址可能无法访问）"),
            _ => Failure($"{action}失败：{exception.Message}{suffix}")
        };
    }

    /// <summary>
    /// 是否为导航失败。
    /// <para>
    /// 按类型名判定而不直接引用 <c>NavigationException</c>：该类型在部分 PuppeteerSharp 版本里是
    /// internal，跨版本引用会编译失败；这里只需要「能识别」，不需要强类型。
    /// </para>
    /// </summary>
    private static bool IsNavigationFailure(Exception exception)
        => exception.GetType().Name is "NavigationException" or "NavigationException`1";

    /// <summary>
    /// 弹窗提示后缀：页面弹过 alert/confirm/prompt 时告诉模型「它被自动关掉了」。
    /// <para>不说明的话，模型会以为「点了没反应」而重复点击。</para>
    /// </summary>
    protected string SessionDialogSuffix(string? sessionId = null, BrowserPageHandle? page = null)
    {
        var handle = page ?? (sessionId is null ? null : Host.TryGetPage(sessionId));
        if (handle is null)
            return string.Empty;

        var dialogs = handle.DrainDialogs();
        return dialogs.Count == 0
            ? string.Empty
            : $"\n（页面弹窗已被自动关闭：{string.Join("；", dialogs)}）";
    }

    /// <summary>把参数里的定位信息读成 <see cref="BrowserElementTarget"/>。</summary>
    protected static BrowserElementTarget ReadTarget(System.Text.Json.JsonElement arguments)
        => new()
        {
            Ref = BrowserJson.ReadString(arguments, "ref"),
            Selector = BrowserJson.ReadString(arguments, "selector"),
            X = BrowserJson.ReadDouble(arguments, "x"),
            Y = BrowserJson.ReadDouble(arguments, "y")
        };

    /// <summary>
    /// 成功结果的统一出口：自动补上「弹窗已被自动关闭」的提示。
    /// </summary>
    /// <remarks>
    /// <b>为什么 sessionId 必须由调用方显式传入</b>：工具是 DI 单例，多个会话并发共享同一实例。
    /// 早期把 sessionId 存在实例字段上（<c>CurrentSessionId</c>），并发时后到的调用会覆盖先到的，
    /// 结果是 A 会话的工具输出里出现 B 会话的弹窗内容（跨会话信息泄漏 + 不可复现竞态）。
    /// 现在会话身份只能沿调用栈传递——编译器会强制每个调用点给出它。
    /// </remarks>
    /// <param name="output">给模型的输出正文。</param>
    /// <param name="sessionId">本次调用所属会话（用于 drain 该会话页面的弹窗队列）。</param>
    /// <param name="metadata">附带的元数据。</param>
    protected ToolResult Success(string output, string? sessionId, Dictionary<string, object>? metadata = null)
        => base.Success(output + DialogSuffix(sessionId), metadata);

    /// <summary>
    /// 带内容段（图片/文件）的成功结果：与
    /// <see cref="Success(string, string?, Dictionary{string, object})"/> 同样自动补弹窗提示。
    /// </summary>
    /// <remarks>
    /// 截图工具必须走这里而不是自己 <c>new ToolResult</c>：否则它在弹窗提示这条链路上
    /// 与其余 11 个工具不一致（模型只在截图路径上不知道页面弹过 alert，会重复点击）。
    /// </remarks>
    protected ToolResult Success(
        string output,
        string? sessionId,
        Dictionary<string, object>? metadata,
        IReadOnlyList<ToolOutputPart> parts)
    {
        var dialogHint = DialogSuffix(sessionId);

        return new ToolResult
        {
            Success = true,
            Output = output + dialogHint,
            Metadata = metadata ?? new Dictionary<string, object>(),
            Parts = parts.Count == 0 ? null : parts
        };
    }

    /// <summary>「弹窗已被自动关闭」的后缀（无弹窗时为空串）。</summary>
    private string DialogSuffix(string? sessionId)
        => sessionId is { Length: > 0 } id && Host.TryGetPage(id) is { } page
            ? SessionDialogSuffix(page: page)
            : string.Empty;
}
