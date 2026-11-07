using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Session.Core;

namespace Seeing.Agent.Hosting.Execution;

/// <summary>
/// <see cref="ExecutionOutcome"/> 组装 — 全项目唯一实现。
/// <para>
/// <b>为什么要集中：</b>终态快照此前在 <see cref="SessionRuntime"/> 与 <see cref="ExecutionRunner"/>
/// 各组装一份，两处逐字相同却已分叉出两种 <c>Duration</c> 口径（墙钟 vs 模型循环累计），
/// 同名字段在不同通道含义不同——消费方（任务卡、观察者、账本）只能靠读注释判断该用哪个。
/// 这里把口径做成显式参数与具名属性，两个调用方各自传入自己掌握的时长。
/// </para>
/// <para>
/// <b>事实字段一律从 <see cref="ExecutionRecord"/> 搬运</b>：<c>AgentName</c>/<c>AgentExecuted</c>/
/// <c>TotalSteps</c>/<c>Usage</c> 是「这一轮到底发生了什么」的权威事实。漏读会让
/// <c>DeliverAndAwaitAsync</c> 恒报「没执行 Agent、零消耗」，下游账本与反思节奏全部失真。
/// 本类不臆造任何数字：记录里是默认值就搬默认值。
/// </para>
/// </summary>
internal static class ExecutionOutcomeFactory
{
    /// <summary>
    /// 从执行记录组装终态快照。
    /// </summary>
    /// <param name="record">执行记录（<c>finally</c> 已落定终态）。</param>
    /// <param name="services">用于解析会话/会话组管理器（<c>Get</c> 命中内存缓存，不产生磁盘 IO）。</param>
    /// <param name="logger">日志（非终态收口与关系读取失败时告警）。</param>
    /// <param name="duration">
    /// 本通道的时长口径：
    /// <list type="bullet">
    /// <item><b>墙钟</b>（<c>CompletedAt - StartedAt</c>，含排队与工具等待）—— 供 <c>DeliverAndAwaitAsync</c>
    /// 回答「这一轮花了多久」。</item>
    /// <item><b>模型循环累计</b>（执行链汇总 <c>LoopCompleteEvent</c>）—— 供
    /// <c>ExecutionCompleteEvent.Outcome</c> 回答「模型跑了多久」。</item>
    /// </list>
    /// 传 null 表示由本方法按墙钟计算（<see cref="SessionRuntime"/> 的通道）。
    /// </param>
    /// <param name="agentExecuted">本轮是否真的执行过 Agent（命令短路时为 false）。</param>
    /// <param name="agentName">实际执行的 Agent 名（仅在 <paramref name="agentExecuted"/> 为真时生效）。</param>
    /// <param name="totalSteps">模型循环步数。</param>
    /// <param name="usage">累计用量。</param>
    public static ExecutionOutcome Create(
        ExecutionRecord record,
        IServiceProvider services,
        ILogger logger,
        TimeSpan? duration = null,
        bool? agentExecuted = null,
        string? agentName = null,
        int? totalSteps = null,
        TokenUsage? usage = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logger);

        // 非终态不该被组装成 outcome（契约：只陈述已发生的事实）。
        // 正常情况下调用点已落定状态；此处是最后一道防线——把漏网的非终态标为 Failed 并告警，
        // 而不是把 Running/Pending 当成终态上报给调用方。
        var status = record.Status;
        if (status is ExecutionStatus.Running or ExecutionStatus.Pending)
        {
            logger.LogWarning(
                "组装 outcome 时记录仍为非终态，按 Failed 收口: ExecutionId={ExecutionId}, Status={Status}",
                record.ExecutionId, status);
            status = ExecutionStatus.Failed;
        }

        var completedAt = record.CompletedAt ?? DateTime.UtcNow;

        TimeSpan resolvedDuration;
        if (duration is { } explicitDuration)
        {
            resolvedDuration = explicitDuration;
        }
        else
        {
            resolvedDuration = completedAt - record.StartedAt;
            if (resolvedDuration < TimeSpan.Zero)
                resolvedDuration = TimeSpan.Zero;
        }

        var (isTopLevel, parentSessionId, origin) = ResolveSessionRelation(services, record.SessionId, logger);

        // 事实字段：调用方显式给了就用调用方的（ExecutionRunner 掌握 loop 侧事实），
        // 否则从记录搬运（SessionRuntime 通道——记录即为唯一事实源）。
        var executed = agentExecuted ?? record.AgentExecuted;
        var name = agentExecuted is null ? record.AgentName : (executed ? agentName : null);

        return new ExecutionOutcome
        {
            SessionId = record.SessionId,
            ExecutionId = record.ExecutionId,
            Status = status,
            AgentName = name,
            AgentExecuted = executed,
            TotalSteps = totalSteps ?? record.TotalSteps,
            Usage = usage ?? record.Usage,
            IsTopLevel = isTopLevel,
            ParentSessionId = parentSessionId,
            Origin = origin,
            Duration = resolvedDuration,
            CompletedAt = completedAt
        };
    }

    /// <summary>
    /// 解析「是否顶层 + 父会话 Id + 会话来源」。
    /// <para>
    /// 真值来源是 <see cref="SessionData.Kind"/>（<c>SubAgent</c> → 非顶层）、会话组的父子关系、
    /// 以及 <see cref="SessionData.Origin"/>；<b>不得</b>读 <c>ChatExecutionContext.IsTopLevel</c>
    /// （该字段在唯一构造点从未赋值，恒为默认 true）。
    /// </para>
    /// <para>
    /// 三者<b>同一次</b>读取会话得出：把 <c>Origin</c> 一并下发，观察者就不必为了判定来源再回读会话
    /// （终态钩子上不该出现第二次 IO）。<see cref="ISessionManager.Get"/> 命中内存缓存，不产生磁盘访问。
    /// </para>
    /// <para>读不到会话时按「顶层 + 用户来源」处理（保守：不把根会话误判成子 Agent 或内部会话）。</para>
    /// </summary>
    public static (bool IsTopLevel, string? ParentSessionId, SessionOrigin Origin) ResolveSessionRelation(
        IServiceProvider services,
        string sessionId,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(services);

        try
        {
            var session = services.GetService<ISessionManager>()?.Get(sessionId);
            if (session is null)
                return (true, null, SessionOrigin.User);

            var origin = session.Origin;
            if (session.Kind != SessionKind.SubAgent)
                return (true, null, origin);

            var groupManager = services.GetService<ISessionGroupManager>();
            var parentId = groupManager is not null && groupManager.TryGetParent(sessionId, out var parent)
                ? parent
                : null;
            return (false, parentId, origin);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "读取会话关系失败，按顶层处理: SessionId={SessionId}", sessionId);
            return (true, null, SessionOrigin.User);
        }
    }
}
