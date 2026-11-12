using Microsoft.Extensions.Options;
using Seeing.Agent.Reflection.Abstractions;
using Seeing.Agent.Reflection.Configuration;
using Seeing.Session.Core;
using System;

namespace Seeing.Agent.Reflection.Core;

/// <summary>
/// 手动复盘入口实现：校验 → 入队 → 唤醒后台 Worker。
/// <para>
/// 只在校验阶段读内存会话（<see cref="ISessionManager.Get"/>），不做磁盘加载：
/// 用户点击的是「当前这个会话」，它必然在内存里；不在内存说明会话已被淘汰或 ID 有误，
/// 直接给出可读原因比静默入队后由 Worker 报 <c>no-source</c> 更有用。
/// </para>
/// </summary>
public sealed class ReflectionManualTrigger : IReflectionManualTrigger
{
    private readonly IReflectionQueue _queue;
    private readonly ISessionManager _sessions;
    private readonly ReflectionModuleActivity _activity;
    private readonly IOptionsMonitor<ReflectionOptions> _options;
    private readonly ReflectionWakeSignal? _wake;

    public ReflectionManualTrigger(
        IReflectionQueue queue,
        ISessionManager sessions,
        ReflectionModuleActivity activity,
        IOptionsMonitor<ReflectionOptions> options,
        ReflectionWakeSignal? wake = null)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _wake = wake;
    }

    /// <inheritdoc />
    public ReflectionManualTriggerResult Trigger(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return new(ReflectionManualTriggerStatus.Invalid, "缺少会话 ID。");

        if (!_activity.IsActive)
            return new(ReflectionManualTriggerStatus.Invalid, "复盘模块未启用：请在启动能力集里包含 reflection。");

        if (_options.CurrentValue.Mode == ReflectionMode.Off)
            return new(ReflectionManualTriggerStatus.Invalid, "复盘模式为 Off：请先在设置中改为「只建议」或「允许创建」。");

        var session = _sessions.Get(sessionId);
        if (session is null)
            return new(ReflectionManualTriggerStatus.Invalid, "会话不在内存中（可能已被回收）：请先在会话里发一条消息再试。");

        if (session.Kind != SessionKind.Root)
            return new(ReflectionManualTriggerStatus.Invalid, "只能复盘顶层会话。");

        if (!session.Origin.IsAutomatableOrigin())
            return new(ReflectionManualTriggerStatus.Invalid, "复盘会话自身不能被复盘。");

        var queued = _queue.TryEnqueue(new ReflectionRequest(
            sessionId,
            string.Empty,
            ReflectionTriggerKind.Manual,
            DateTimeOffset.UtcNow));

        if (queued is ReflectionEnqueueResult.Enqueued or ReflectionEnqueueResult.Updated)
            _wake?.Pulse();

        return queued switch
        {
            ReflectionEnqueueResult.Enqueued =>
                new(ReflectionManualTriggerStatus.Enqueued, "已加入复盘队列，稍后到「复盘记录」查看结果。"),
            ReflectionEnqueueResult.Updated =>
                new(ReflectionManualTriggerStatus.Updated, "该会话已在队列中，已更新为最新请求。"),
            ReflectionEnqueueResult.Dropped =>
                new(ReflectionManualTriggerStatus.Dropped, "复盘队列已满，请稍后再试。"),
            _ => new(ReflectionManualTriggerStatus.Invalid, "会话 ID 无效。")
        };
    }
}
