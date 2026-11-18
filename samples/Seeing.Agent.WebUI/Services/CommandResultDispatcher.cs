using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Events;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 命令结果分派：把 <see cref="CommandResultEvent"/> 转成宿主动作执行或临时提示。
/// <para>
/// 规则（与设计文档 §6.3 一致）：
/// <list type="bullet">
/// <item>有动作：执行；<c>Handled</c> 显示动作返回的提示（不重复命令原文），
/// <c>Unavailable</c> 显示不可用原因，<c>NotHandled</c> 回退命令原文。</item>
/// <item>无动作：直接显示命令原文。</item>
/// </list>
/// 只负责“显示什么”，导航与时间线刷新仍由窗口按原有事件分支处理。
/// </para>
/// </summary>
public sealed class CommandResultDispatcher
{
    private readonly IHostActionRegistry _actions;
    private readonly MessageTimelineStore _timeline;
    private readonly ILogger<CommandResultDispatcher> _logger;

    /// <summary>创建分派器。</summary>
    public CommandResultDispatcher(
        IHostActionRegistry actions,
        MessageTimelineStore timeline,
        ILogger<CommandResultDispatcher> logger)
    {
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        _timeline = timeline ?? throw new ArgumentNullException(nameof(timeline));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>处理命令结果事件（只产生临时提示，不落盘）。</summary>
    public async Task DispatchAsync(string sessionId, CommandResultEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        if (evt.Action is { } action)
        {
            var result = await _actions.InvokeAsync(sessionId, action).ConfigureAwait(false);
            switch (result.Status)
            {
                case HostActionStatus.Handled:
                    if (!string.IsNullOrWhiteSpace(result.Message))
                        Append(evt, result.Message!, isError: !evt.Success);
                    return;

                case HostActionStatus.Unavailable:
                    Append(
                        evt,
                        result.Message ?? evt.Message ?? "当前宿主不支持该操作",
                        isError: false);
                    return;

                default:
                    _logger.LogDebug(
                        "宿主动作未注册，回退命令文本: SessionId={SessionId}, Command={Command}, Action={Action}",
                        sessionId,
                        evt.CommandName,
                        action.Name);
                    break;
            }
        }

        if (!string.IsNullOrWhiteSpace(evt.Message))
            Append(evt, evt.Message!, isError: !evt.Success);
    }

    private void Append(CommandResultEvent evt, string text, bool isError)
    {
        var key = $"cmd:{evt.CommandName}:{evt.Timestamp.Ticks}";
        _timeline.AppendTransientNotice(key, text, isError);
    }
}
