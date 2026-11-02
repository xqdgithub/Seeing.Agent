using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Reflection.Abstractions;
using Seeing.Agent.Reflection.Configuration;
using Seeing.Agent.Reflection.Core;
using Seeing.Session.Core;

namespace Seeing.Agent.Reflection.Integration;

/// <summary>
/// 执行终态观察者：按轮次节奏把「需要复盘」的会话放进队列。
/// <para>
/// 硬性约束（见 <see cref="IExecutionOutcomeObserver"/>）：<b>禁止</b>任何磁盘 I/O 与会话回读，
/// 本类只读 <see cref="ExecutionOutcome"/> 上的事实并做一次内存计数 + 入队。
/// </para>
/// </summary>
public sealed class ReflectionObserver : IExecutionOutcomeObserver
{
    /// <summary>复盘执行所用 Agent 名称；用于阻止复盘会话自身再次触发复盘。</summary>
    public const string ReflectionAgentName = "reflection";

    private readonly IOptionsMonitor<ReflectionOptions> _options;
    private readonly ReflectionTurnCounter _turns;
    private readonly IReflectionQueue _queue;
    private readonly ReflectionModuleActivity _activity;
    private readonly ILogger<ReflectionObserver>? _logger;

    public ReflectionObserver(
        IOptionsMonitor<ReflectionOptions> options,
        ReflectionTurnCounter turns,
        IReflectionQueue queue,
        ReflectionModuleActivity activity,
        ILogger<ReflectionObserver>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _turns = turns ?? throw new ArgumentNullException(nameof(turns));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
        _logger = logger;
    }

    /// <summary>
    /// 判定本轮终态是否需要计入复盘节奏。
    /// <para>判定顺序即优先级：先看「功能是否开启」（最便宜且最能提前短路），
    /// 再看执行事实，最后看来源防递归。</para>
    /// </summary>
    /// <param name="outcome">本轮执行的终态事实。</param>
    public bool ShouldObserve(ExecutionOutcome outcome)
    {
        if (outcome is null)
            return false;

        var options = _options.CurrentValue;

        // 1. 总开关：未开启时一切归零，不必计算后续条件。
        if (!options.Enabled)
            return false;

        // 2. 模式为 Off 等价于关闭（Enabled 与 Mode 双重开关，避免误配后偷偷运行）。
        if (options.Mode == ReflectionMode.Off)
            return false;

        // 3. 未真正执行 Agent（命令短路 / 纯本地回复）的轮次没有可复盘内容。
        if (!outcome.AgentExecuted)
            return false;

        // 4. 只复盘顶层会话：子代理轮次属于父轮次的内部细节，重复复盘会放大噪声。
        if (!outcome.IsTopLevel)
            return false;

        // 5. 防递归第 1 重：复盘 Agent 自己的轮次不得再触发复盘。
        if (string.Equals(outcome.AgentName, ReflectionAgentName, StringComparison.OrdinalIgnoreCase))
            return false;

        // 6. 防递归第 2 重：来源为复盘的会话一律跳过（即使 Agent 名不同）。
        if (outcome.Origin == SessionOrigin.Reflection)
            return false;

        // 7. 失败 / 取消的轮次不复盘：那些轮次的事实不完整，复盘只会得出错误结论。
        if (outcome.Status != ExecutionStatus.Completed)
            return false;

        // 8. 无会话 ID 无法定位目标，直接放弃。
        if (string.IsNullOrWhiteSpace(outcome.SessionId))
            return false;

        return true;
    }

    /// <inheritdoc />
    public Task OnExecutionCompletedAsync(ExecutionOutcome outcome, CancellationToken ct = default)
    {
        // 观察者异常一律吞掉：终态观察是尽力而为的旁路动作，绝不能影响主链终态。
        try
        {
            if (!_activity.IsActive)
                return Task.CompletedTask;

            // 防御性幂等：调用方可能未先经过 ShouldObserve。
            if (!ShouldObserve(outcome))
                return Task.CompletedTask;

            var options = _options.CurrentValue;
            var interval = Math.Max(1, options.TurnNudgeInterval);
            var count = _turns.Increment(outcome.SessionId);

            if (count < interval)
                return Task.CompletedTask;

            _turns.Reset(outcome.SessionId);

            var queued = _queue.TryEnqueue(new ReflectionRequest(
                outcome.SessionId,
                outcome.ExecutionId,
                ReflectionTriggerKind.TurnNudge,
                DateTimeOffset.UtcNow));

            if (!queued)
            {
                // 队列满或同会话已在排队：属于正常运行态，记调试日志即可。
                _logger?.LogDebug(
                    "复盘请求未新增（队列已满或会话已在队列）：SessionId={SessionId}",
                    outcome.SessionId);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex,
                "复盘观察者处理终态失败（已忽略，不影响执行链）：SessionId={SessionId}, ExecutionId={ExecutionId}",
                outcome?.SessionId,
                outcome?.ExecutionId);
        }

        return Task.CompletedTask;
    }
}
