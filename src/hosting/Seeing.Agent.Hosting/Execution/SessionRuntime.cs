using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Channels;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Models;
using Seeing.Agent.Abstractions.Reminders;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Agent.Abstractions.Sessions;
using Seeing.Agent.Core.Execution;
using Seeing.Serialization;
using Seeing.Session.Core;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Seeing.Agent.Hosting.Execution;

/// <summary>
/// 会话运行时 - 统一的「往会话放消息并激活」入口（骨架，实现见 2.3/2.4）。
/// </summary>
public sealed class SessionRuntime : ISessionRuntime, IExecutionInFlightBoundary, IDisposable, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, SessionExecutionState> _states = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionGates = new();
    private readonly ConcurrentDictionary<string, Task> _activeLoops = new();
    private readonly ConcurrentDictionary<string, byte> _stoppedSessions = new();
    private readonly SemaphoreSlim _globalConcurrency;
    private readonly IExecutionRunner _runner;
    private readonly IExecutionEventPublisher _publisher;
    private readonly IServiceProvider _services;
    private readonly ExecutionOptions _options;
    private readonly ILogger<SessionRuntime> _logger;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastActive = new();

    /// <summary>
    /// 待等待的终态订阅者：key = 入站消息 Id。
    /// <para>
    /// 为什么绑消息 Id 而不是 executionId：executionId 只在消费循环真正开跑时才产生，
    /// 投递返回时无从得知。而每条消息只会被某一批消费恰一次，因此「消息 Id → 消费它的那一轮执行」
    /// 天然唯一，连续投递不会串台。
    /// </para>
    /// </summary>
    private readonly ConcurrentDictionary<string, ExecutionAwaiter> _awaiters = new();

    private readonly Timer? _cleanupTimer;
    private int _disposed;

    /// <summary>WaitForIdleAsync 安全上限（防死循环；正常路径由调用方 CancellationToken 控制）。</summary>
    private static readonly TimeSpan WaitForIdleSafetyTimeout = TimeSpan.FromMinutes(10);

    internal SessionRuntime(
        IExecutionRunner runner,
        IExecutionEventPublisher publisher,
        IServiceProvider services,
        ExecutionOptions options,
        ILogger<SessionRuntime> logger)
    {
        _runner = runner;
        _publisher = publisher;
        _services = services;
        _options = options;
        _logger = logger;
        _globalConcurrency = new SemaphoreSlim(options.MaxConcurrentExecutions <= 0 ? int.MaxValue : options.MaxConcurrentExecutions);

        if (options.CleanupInterval > TimeSpan.Zero)
        {
            _cleanupTimer = new Timer(
                _ => { CleanupIdleSessions(); },
                state: null,
                dueTime: options.CleanupInterval,
                period: options.CleanupInterval);
        }
    }

    public async Task<InboxResult> DeliverAsync(InboxMessage message, CancellationToken ct = default)
        => await DeliverCoreAsync(message, onMessagePersisted: null, ct).ConfigureAwait(false);

    /// <summary>
    /// 投递并等待本次投递所消费的那一轮执行终态：把「投递 + 猜最近一次执行」收敛成一个契约。
    /// <para>
    /// 等待与**消息 Id**绑定（而非 executionId，后者只在消费循环开跑时才产生）；
    /// 同一条消息被合并进哪一批执行，就由那一批执行完成等待，因此连续投递不会串台。
    /// </para>
    /// <para>
    /// 只接受「会真的被消费循环消费」的投递：<see cref="InboxDelivery.Wake"/> +
    /// 非「生来已消费」的 presentation。否则等待永远没有对象，必然挂起。
    /// </para>
    /// </summary>
    public async Task<ExecutionOutcome> DeliverAndAwaitAsync(
        InboxMessage message, CancellationToken ct = default)
    {
        if (message is null)
            throw new ArgumentNullException(nameof(message));

        // 令牌已取消时直接返回，避免「取消了还落盘一条消息」。
        ct.ThrowIfCancellationRequested();

        if (message.Delivery != InboxDelivery.Wake)
        {
            throw new ArgumentException(
                "DeliverAndAwaitAsync 仅支持 Wake 投递：Inject/Notify 不产生执行，等待必然落空。",
                nameof(message));
        }

        // 落盘即标记 consumed 的 presentation（Assistant 外部产出写回 / None 展示性横幅）
        // 永远进不了未读集合，消费循环不会为它开一轮——等待必然挂起，故在入口拒绝。
        // 判定与 BuildInboxMessage 共用 IsBornConsumed：规则只有一处，不会漂移。
        if (IsBornConsumed(message.Presentation))
        {
            throw new ArgumentException(
                $"DeliverAndAwaitAsync 不支持 Presentation.{message.Presentation}：该 presentation 落盘即已消费，" +
                "不参与未读、不会触发执行，等待必然落空。",
                nameof(message));
        }

        // 会话 Id 非法：交给 DeliverAsync 产出失败结果，再按契约转异常（错误文案单一来源）。
        // 空 Id 必然失败，不存在「成功但没有执行可绑定」的情形，故一律抛。
        if (string.IsNullOrWhiteSpace(message.TargetSessionId))
        {
            var invalid = await DeliverAsync(message, ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                invalid.Success ? "TargetSessionId 为空，无法等待执行终态。" : invalid.Error ?? "入站投递失败。");
        }

        var awaiter = new ExecutionAwaiter(message.TargetSessionId);

        // 取消（含调用方超时）直接让等待任务取消；把 ct 传给 TCS，保留「谁取消的」信息。
        // registration 必须注销，避免长期持有回调。
        using var cancellationRegistration = ct.Register(
            static state =>
            {
                var (target, token) = ((ExecutionAwaiter, CancellationToken))state!;
                target.TrySetCanceled(token);
            },
            (awaiter, ct));

        InboxResult result;
        try
        {
            // 关键顺序：等待槽在消息落盘**之前**登记（回调在建好 SessionMessage 后立即触发），
            // 因此消费循环不可能先看到这条消息、再被我们登记——消除「先完成、后登记」的竞态。
            result = await DeliverCoreAsync(
                message, persistedId => RegisterAwaiter(persistedId, awaiter), ct).ConfigureAwait(false);
        }
        catch
        {
            RemoveAwaiter(awaiter);
            throw;
        }

        if (!result.Success)
        {
            RemoveAwaiter(awaiter);
            throw new InvalidOperationException(result.Error ?? "入站投递失败。");
        }

        if (awaiter.MessageId is null)
        {
            // 幂等键命中：本次没有产生新的落盘消息，也就没有可绑定的那一轮执行。
            RemoveAwaiter(awaiter);
            throw new InvalidOperationException(
                "投递被幂等键短路，未产生新的执行，无法等待终态；请改用 DeliverAsync 后自行判定。");
        }

        // 消息已落盘：此后会话级的停止清扫才能作用于本等待槽。
        awaiter.MarkPersisted();

        // 竞态兜底：落盘瞬间会话已被 Stop，且消费循环已因停止退出 → 不会再有执行兑现本次等待。
        // 必须显式收口，否则「Stop 后才落盘」的等待没有任何兜底。
        if (_stoppedSessions.ContainsKey(message.TargetSessionId))
        {
            RemoveAwaiter(awaiter);
            throw new OperationCanceledException("会话已停止，本次投递不会产生执行终态。");
        }

        try
        {
            return await awaiter.Completion.Task.ConfigureAwait(false);
        }
        finally
        {
            RemoveAwaiter(awaiter);
        }
    }

    /// <summary>
    /// <see cref="DeliverAsync"/> 的实际实现。
    /// <paramref name="onMessagePersisted"/> 在入站消息构造完成、**落盘之前**以消息 Id 回调，
    /// 供等待方抢占登记位置（避免消费循环先消费、等待方后登记的竞态）。
    /// </summary>
    private async Task<InboxResult> DeliverCoreAsync(
        InboxMessage message, Action<string>? onMessagePersisted, CancellationToken ct)
    {
        if (message is null)
            return InboxResult.Failed("InboxMessage is required");
        if (string.IsNullOrWhiteSpace(message.TargetSessionId))
            return InboxResult.Failed("TargetSessionId is required");

        var sessionId = message.TargetSessionId;
        Touch(sessionId);

        using var scope = _services.CreateScope();
        var sessionManager = scope.ServiceProvider.GetRequiredService<ISessionManager>();

        // 1. 确保目标会话存在（缓存 → 存储 → 自动创建，对齐旧 SubmitAsync 的 EnsureSessionAsync 语义）
        var session = await sessionManager.EnsureSessionAsync(
            sessionId,
            scenario: scope.ServiceProvider.GetService<IDefaultWorkModeProvider>()?.GetDefaultScenario())
            .ConfigureAwait(false);

        // 2. 鉴权：跨组鉴权由调用方（工具层 session_inbox_send 走 tool.execute 门）负责；
        //    运行时入口放行（与 spec §11「缺失授权器则放行」一致）。

        // 3. 幂等预检：同键重复投递复用首次结果，不重复落盘
        var idemKey = string.IsNullOrWhiteSpace(message.IdempotencyKey)
            ? null
            : $"inbox.idem:{message.IdempotencyKey!.Trim()}";
        if (idemKey is not null && session.Metadata.TryGetValue(idemKey, out var cached))
        {
            var cachedMessageId = cached.Contains('|') ? cached.Split('|', 2)[0] : cached;
            return InboxResult.Ok(messageId: cachedMessageId, pendingInputCount: CountUnread(session));
        }

        // 4. 落盘消息（Presentation → Role 映射 + inbox.* 元数据）
        // 落盘判定基于 Delivery：Notify 仅出 UI 通知不落盘；Inject/Wake 均落盘（含 Presentation=None，
        // 如 Gateway 取消/错误横幅——可持久化展示但不进 LLM 历史）。
        string? persistedMessageId = null;
        if (message.Delivery != InboxDelivery.Notify)
        {
            var persisted = BuildInboxMessage(message, scope.ServiceProvider);
            persistedMessageId = persisted.Id;

            // 等待方先占位（尚未落盘，消费循环不可能已消费该条）→ 无「先完成后登记」窗口。
            onMessagePersisted?.Invoke(persisted.Id);

            await sessionManager.AddMessageAsync(sessionId, persisted, ct).ConfigureAwait(false);
        }

        // 5. Sender=Human 且目标会话有父 → 置 Owner=Human（接管）
        if (message.Sender.Kind == InboxSenderKind.Human)
        {
            try
            {
                var groupManager = scope.ServiceProvider.GetRequiredService<ISessionGroupManager>();
                var parentId = await groupManager.GetParentAsync(sessionId, ct).ConfigureAwait(false);
                if (parentId is not null)
                {
                    session.SetOwner(SessionOwner.Human, message.Sender.Id);
                    await sessionManager.SaveAsync(sessionId).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "接管标记失败: SessionId={SessionId}", sessionId);
            }
        }

        // 6. 幂等键记录：会话级 Metadata（统一落点，见 spec §5.2）
        if (idemKey is not null && persistedMessageId is not null)
        {
            session.Metadata[idemKey] = persistedMessageId;
            await sessionManager.SaveAsync(sessionId).ConfigureAwait(false);
        }

        // 7. 按 Delivery 分发
        switch (message.Delivery)
        {
            case InboxDelivery.Notify:
                // 只出 UI 通知，不唤醒、不落盘（见步骤 4）
                PublishNotice(message);
                return InboxResult.Ok(messageId: persistedMessageId);

            case InboxDelivery.Inject:
                // 只落盘，不启动循环
                return InboxResult.Ok(messageId: persistedMessageId, pendingInputCount: CountUnread(session));

            case InboxDelivery.Wake:
            default:
                // 在 gate 内：清除停止态 + control.stop_reason；按需启动消费循环（单飞）
                var gate = _sessionGates.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    _stoppedSessions.TryRemove(sessionId, out _);
                    if (session.Metadata.Remove(SessionControlKeys.StopReason))
                        await sessionManager.SaveAsync(sessionId).ConfigureAwait(false);

                    if (!_activeLoops.ContainsKey(sessionId))
                    {
                        // 先注册后运行（Ready 门控），避免循环体在 `_activeLoops` 登记前就结束造成条目泄漏；
                        // 句柄持有自身 Task 引用，退出时按值移除，避免误删「新循环」条目。
                        var handle = new LoopHandle();
                        var loop = Task.Run(async () =>
                        {
                            await handle.Ready.Task.ConfigureAwait(false);
                            await RunLoopAsync(sessionId, handle, _disposeCts.Token).ConfigureAwait(false);
                        });
                        handle.Loop = loop;
                        _activeLoops[sessionId] = loop;
                        handle.Ready.SetResult();
                    }
                }
                finally
                {
                    gate.Release();
                }

                // 未读输入数（spec：会话当前未消费输入数）；UI 执行中经 GetOverview 实时刷新
                var pendingCount = CountUnread(session);
                _states.GetOrAdd(sessionId, _ => new SessionExecutionState()).PendingInputCount = pendingCount;
                return InboxResult.Ok(messageId: persistedMessageId, pendingInputCount: pendingCount);
        }
    }

    /// <summary>
    /// 消费循环（单飞；每会话串行化）。逐轮持 gate 完成「终止检查 + 未读判定 + 标记 consumed」，
    /// 执行体在 gate 释放后运行；执行中到达的输入留待下一轮。
    /// </summary>
    private async Task RunLoopAsync(string sessionId, LoopHandle handle, CancellationToken ct)
    {
        var gate = _sessionGates.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));

        // 本循环最后一次创建的记录（跨迭代持有）。
        // 外层 finally 只能据此「按身份」清空 CurrentExecution，绝不能无条件清：
        // RemoveLoop 之后投递线程可立刻拉起**新**循环并写入它自己的 CurrentExecution，
        // 无条件清空会抹掉新循环的在途记录，导致 Stop/Interrupt/Dispose 读不到 Cts 而取消失效。
        ExecutionRecord? loopOwnedRecord = null;

        try
        {
            while (true)
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);

                List<SessionMessage> pending;
                try
                {
                    // 终止检查：会话已被 Stop（或已删除）→ 退出循环，不再消费
                    if (_stoppedSessions.ContainsKey(sessionId))
                    {
                        // 兜底：StopAsync 的取消清扫之后才提交的等待槽，在此收口。
                        CancelAwaitersForSession(sessionId, new OperationCanceledException(
                            "会话已停止，执行未产生可用终态。"));
                        RemoveLoop(sessionId, handle);
                        break;
                    }

                    using var scope = _services.CreateScope();
                    var sessionManager = scope.ServiceProvider.GetRequiredService<ISessionManager>();
                    var session = sessionManager.Get(sessionId);
                    if (session is null)
                    {
                        // 会话已被删除：不会再有执行兑现等待。
                        CancelAwaitersForSession(sessionId, new OperationCanceledException(
                            "会话不存在（可能已删除），执行未产生可用终态。"));
                        RemoveLoop(sessionId, handle);
                        break;
                    }

                    pending = session.Messages.Where(IsPendingInboxMessage).ToList();
                    if (pending.Count == 0)
                    {
                        // 【唤醒不丢】投递方先落盘、再抢 gate 检查 _activeLoops；循环在 gate 内
                        // 「读未读 → 摘除 _activeLoops」。两者互斥，所以要么本轮读到该消息，
                        // 要么投递方看到循环已摘除并另起一个——不存在「刚判空闲、消息随后落盘」的漏消费。
                        //
                        // 【但 gate 不覆盖 awaiter 时序】MarkPersisted 在 DeliverCoreAsync 返回之后才执行，
                        // 那时投递方已释放 gate，因此「循环在此处取消等待槽」与本对象的落盘标记
                        // **不在同一临界区**，靠 gate 无法判对。所以这里**完全不取消等待槽**：
                        // 没有未读只是瞬时事实，消息可能已被上一轮消费（等待已兑现）。
                        // 残留等待（消息被第三方 trim/revert 删除）是已知边界，见
                        // ISessionRuntime.DeliverAndAwaitAsync 的 remarks。
                        //
                        // Stop 窗口的闭合依赖**两处独立动作**，缺任一处都会留下挂起：
                        //   ① StopAsync 先把会话加入 _stoppedSessions，再在 gate 外扫描取消**已落盘**的等待槽；
                        //   ② 投递侧在落盘之后、进入等待之前复查 _stoppedSessions 并主动抛取消。
                        // 缺 ① 的漏法：等待槽已落盘且复查通过（当时还没 Stop），随后才发生 Stop，
                        //            再没有扫描去取消它 —— 只能靠调用方 ct 或后续 Dispose 收口。
                        // 缺 ② 的漏法：Stop 恰好落在「消息已落盘、循环尚未消费」的窄窗，
                        //            ① 的扫描因该等待槽当时尚未 Persisted 而跳过它，之后循环退出，
                        //            该次等待永远等不到终态。
                        //
                        // ⚠ 本处**没有单点测试固定**：两处都需要纳秒级时序注入才能构造「只删其一」的失败，
                        //   审查者实测删除 ① 或删除 ② 各自都全绿。改动 ①② 任一处，必须人工推演上述两条漏法，
                        //   不要依赖测试兜底。（诚实标注优于虚假承诺。）
                        RemoveLoop(sessionId, handle);
                        break;
                    }

                    // Stop 二次校验：StopAsync 可能落在「终止检查通过后、标记 consumed 前」。
                    // 必须在标记 consumed 之前判定：命中则退出且不消费本批，避免消息被静默丢失。
                    if (_stoppedSessions.ContainsKey(sessionId))
                    {
                        CancelAwaitersForSession(sessionId, new OperationCanceledException(
                            "会话已停止，执行未产生可用终态。"));
                        RemoveLoop(sessionId, handle);
                        break;
                    }

                    // 单一清除点：本批全部置 consumed=true（执行体启动前一次性写）
                    foreach (var m in pending)
                    {
                        var metadata = m.Metadata ?? ImmutableDictionary<string, object>.Empty;
                        m.Metadata = metadata.SetItem(InboxMetadataKeys.Consumed, true);
                    }
                    await sessionManager.SaveAsync(sessionId).ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }

                // 本批对应的等待者：以「消息是否被本批消费」为锚点，与本轮执行一一绑定。
                // 必须在本批已被标记 consumed 且 gate 释放后取：此时该批已定形，不会再有消息并入。
                var awaiters = TakeAwaitersFor(pending);

                ExecutionRecord? record = null;
                try
                {
                    await _globalConcurrency.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        // Stop 可能落在「全局并发等待期间」——此时 record 尚未创建，取消 record.Cts 无从生效。
                        // 放行后、创建 record 前再次校验停止态；命中则本轮不执行，退出循环。
                        if (_stoppedSessions.ContainsKey(sessionId) || ct.IsCancellationRequested)
                        {
                            // 本轮不执行：本批等待者不会有 outcome，必须显式取消而不是永久挂起。
                            CancelAwaiters(awaiters, new OperationCanceledException(
                                "会话已停止或运行时已释放，本轮执行未启动。"));
                            break;
                        }

                        var executionId = $"exec_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}"[..24];
                        record = new ExecutionRecord
                        {
                            ExecutionId = executionId,
                            SessionId = sessionId,
                            Status = ExecutionStatus.Running,
                            StartedAt = DateTime.UtcNow,
                            Cts = new CancellationTokenSource()
                        };

                        var state = _states.GetOrAdd(sessionId, _ => new SessionExecutionState());
                        state.CurrentExecution = record;
                        state.PendingInputCount = 0;
                        loopOwnedRecord = record;
                        Touch(sessionId);

                        var userMessages = pending
                            .Where(m => string.Equals(
                                GetMetaString(m.Metadata, InboxMetadataKeys.Presentation),
                                "user", StringComparison.OrdinalIgnoreCase))
                            .ToList();
                        var hasUserInput = userMessages.Count > 0;
                        var firstUserMessage = userMessages.FirstOrDefault();
                        var lastUserMessage = userMessages.LastOrDefault();
                        var firstUserText = firstUserMessage?.Content;

                        // 指令注入锚点：本批首条 user 消息 Id（指令插入到用户命令之前）。
                        var batch = new InboxBatch(pending, hasUserInput, firstUserText, firstUserMessage?.Id);

                        // 命令判定与标题统一取本批「最后一条 user 文本」：
                        // 合并消费下命令通常是最后一条（如 hello + /compact），取末条才能识别命令。
                        record.Input = new ChatInput { Text = lastUserMessage?.Content ?? string.Empty };
                        record.Options = new ChatOptions
                        {
                            AgentId = LastNonEmptyMeta(pending, InboxMetadataKeys.AgentId),
                            ModelId = LastNonEmptyMeta(pending, InboxMetadataKeys.ModelId),
                            ChannelRoute = ParseConversationRoute(
                                LastNonEmptyMeta(pending, InboxMetadataKeys.Conversation)),
                            ThinkingEffort = LastNonEmptyMeta(pending, InboxMetadataKeys.ThinkingEffort),
                            ModeId = LastNonEmptyMeta(pending, InboxMetadataKeys.ModeId),
                            WorkingDirectory = LastNonEmptyMeta(pending, InboxMetadataKeys.WorkingDirectory)
                        };

                        // ⚠ 已知边界：此处**丢弃**了 RunOnceAsync 的返回值，只承认入参 record 这一实例。
                        //    接口签名（返回 ExecutionRecord）会诱导未来实现「new 一个新记录返回」——
                        //    那时事实全丢（state.LastExecution / TryCompleteAwaiters 仍看旧实例）。
                        //    当前唯一实现 ExecutionRunner 原地写 record 并原样返回，故安全；
                        //    测试里的 FakeRunner 也返回同一实例，**测不出**这个陷阱。
                        //    若将来要支持返回新实例，必须改成 `record = await _runner.RunOnceAsync(...)`。
                        await _runner.RunOnceAsync(record, batch, record.Cts.Token).ConfigureAwait(false);
                        state.LastExecution = record;
                        TryCompleteAwaiters(sessionId, record, awaiters);
                    }
                    finally
                    {
                        // 按身份清空：只有 CurrentExecution 仍是我这一轮的记录才清。
                        // 无条件置 null 会把「另一条路径刚写入的记录」一并抹掉（与 TeardownLoop 同型缺陷）。
                        ClearRoundCurrentExecution(sessionId, record);

                        // 本轮终态后释放专属 CTS：RunOnceAsync 已返回（含取消路径），执行体不再持有令牌。
                        // Stop/Interrupt 与释放存在窄竞态，二者均捕获 ObjectDisposedException，视为已终态。
                        if (record is not null)
                        {
                            record.Cts?.Dispose();
                            record.Cts = null;
                        }
                        _globalConcurrency.Release();
                    }
                }
                catch (OperationCanceledException)
                {
                    // Interrupt/Stop 取消：本执行已终态；consumed 不回退。
                    // Interrupt → 循环继续消费剩余未读；Stop → 下轮终止检查命中退出。
                    if (record is not null)
                    {
                        // 执行体可能是「以 Running 状态被取消」：必须落定 Cancelled，
                        // 否则会把非终态当成终态组装成 outcome。
                        if (record.Status is ExecutionStatus.Running or ExecutionStatus.Pending)
                            record.Status = ExecutionStatus.Cancelled;
                        record.CompletedAt ??= DateTime.UtcNow;

                        _states.GetOrAdd(sessionId, _ => new SessionExecutionState()).LastExecution = record;
                        TryCompleteAwaiters(sessionId, record, awaiters);
                    }
                    else
                    {
                        // 记录都没建出来（全局并发等待期被取消）：本批等待者不会有 outcome。
                        CancelAwaiters(awaiters, new OperationCanceledException(
                            "执行未启动即被取消。"));
                    }
                }
                catch (Exception ex)
                {
                    // 执行失败：记录失败，继续循环消费剩余未读（避免整循环崩溃）
                    _logger.LogError(ex, "消费循环执行失败: SessionId={SessionId}", sessionId);
                    if (record is not null)
                    {
                        // 失败必须落定状态：_runner 抛异常时 record 仍是 Running，
                        // 若原样搬进 outcome 就等于把「非终态」当终态上报。
                        record.Status = ExecutionStatus.Failed;
                        record.ErrorMessage ??= ex.Message;
                        record.CompletedAt ??= DateTime.UtcNow;

                        _states.GetOrAdd(sessionId, _ => new SessionExecutionState()).LastExecution = record;
                        TryCompleteAwaiters(sessionId, record, awaiters);
                    }
                    else
                    {
                        // 同上：没有 record 就没有 outcome；显式失败而非永久挂起。
                        CancelAwaiters(awaiters, ex);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 运行时释放（或 gate/并发等待被取消）导致循环整体退出：
            // 本会话残留的等待再无人兑现，必须显式收口，不能让它永久挂起。
            _logger.LogDebug("消费循环被取消退出: SessionId={SessionId}", sessionId);
            CancelAwaitersForSession(sessionId, new OperationCanceledException(
                "消费循环已退出（运行时释放或会话停止），执行未产生可用终态。"));
        }
        finally
        {
            TeardownLoop(sessionId, handle, loopOwnedRecord);
        }
    }

    /// <summary>
    /// 轮内收尾：本轮执行结束后按**记录身份**清空 <c>CurrentExecution</c>。
    /// <para>
    /// 与 <see cref="TeardownLoop"/> 是同型守卫但位置不同：这里覆盖「正常跑完一轮」，
    /// 那里覆盖「循环整体退出（break/OCE）」。两处都必须按身份，否则会抹掉另一条路径刚写入的记录。
    /// </para>
    /// <para>internal 供测试直接钉住轮内守卫（顶层守卫由 <see cref="TeardownLoop"/> 单测覆盖）。</para>
    /// </summary>
    internal void ClearRoundCurrentExecution(string sessionId, ExecutionRecord? record)
    {
        if (record is null)
            return;

        // 接线可观测点：证明逐轮 finally 确实带着本轮记录进来（而非恒传 null）。
        // 没有它时「轮内清空被整体删掉」不会被状态断言发现——下一轮会重新赋值 CurrentExecution。
        Interlocked.Increment(ref _roundClearWithRecordCount);

        if (_states.TryGetValue(sessionId, out var state) &&
            ReferenceEquals(state.CurrentExecution, record))
        {
            state.CurrentExecution = null;
        }
    }

    /// <summary>
    /// 循环收尾（**唯一**的循环退出清理入口）：摘除本循环的活跃登记 + 按记录身份清理运行态。
    /// <para>
    /// <b>为什么按身份清</b>：<see cref="RemoveLoop"/> 摘除后，投递线程可立刻拉起**新**循环并为它写入
    /// 自己的 <c>CurrentExecution</c>；若此处无条件置 null，会抹掉新循环的在途记录，使
    /// Stop/Interrupt/Dispose 读不到 <c>Cts</c>、取消对该轮彻底失效，也让 CleanupIdleSessions
    /// 误判空闲清掉运行态。<paramref name="owned"/> 为本循环最后一次创建的记录（可能为 null）。
    /// </para>
    /// <para>
    /// internal 且接收 <paramref name="handle"/> 与 <paramref name="owned"/> 而非在方法内读取：
    /// 这样测试能直接调用**真实收尾接线**构造「旧循环收尾 vs 新循环已写入」的交错，
    /// 而不是只测一个可被孤立的 helper（上一版正因只测 helper，回归仍无 CI 信号）。
    /// </para>
    /// </summary>
    /// <param name="sessionId">会话 Id。</param>
    /// <param name="handle">本循环的句柄（值匹配摘除，不会误删新循环条目）。</param>
    /// <param name="owned">本循环最后创建的记录；null 表示本循环没有创建过任何记录（无需清理）。</param>
    internal void TeardownLoop(string sessionId, LoopHandle handle, ExecutionRecord? owned)
    {
        RemoveLoop(sessionId, handle);

        if (owned is null)
            return;

        // 接线可观测点：证明 RunLoopAsync 确实把本轮记录交给了收尾（而非恒传 null）。
        // 缺此计数时「loopOwnedRecord 永不赋值」不会被任何断言发现——外层的防御性清理
        // 在正常流程里被逐轮 finally 掩盖，只有这里能区分「真的接了线」与「接线被删」。
        Interlocked.Increment(ref _teardownWithOwnedCount);

        if (_states.TryGetValue(sessionId, out var state) &&
            ReferenceEquals(state.CurrentExecution, owned))
        {
            state.CurrentExecution = null;
        }
    }

    private int _teardownWithOwnedCount;
    private int _roundClearWithRecordCount;

    #region 测试接缝（仅供测试项目，经 InternalsVisibleTo 可见；不参与生产行为）

    /// <summary>测试用：返回「收尾时确实带着本循环记录」的累计次数（用于钉住 RunLoopAsync 的接线）。</summary>
    internal int TeardownWithOwnedCount => Volatile.Read(ref _teardownWithOwnedCount);

    /// <summary>测试用：返回「轮内收尾确实带着本轮记录」的累计次数（用于钉住逐轮 finally 的接线）。</summary>
    internal int RoundClearWithRecordCount => Volatile.Read(ref _roundClearWithRecordCount);

    /// <summary>测试用：直接写入某会话的 CurrentExecution（构造跨循环交错的确定性场景）。</summary>
    internal void SetCurrentExecutionForTest(string sessionId, ExecutionRecord record) =>
        _states.GetOrAdd(sessionId, _ => new SessionExecutionState()).CurrentExecution = record;

    /// <summary>测试用：直接读取某会话的 CurrentExecution。</summary>
    internal ExecutionRecord? GetCurrentExecutionForTest(string sessionId) =>
        _states.TryGetValue(sessionId, out var state) ? state.CurrentExecution : null;

    /// <summary>
    /// 测试用：该会话是否仍有活跃循环登记。
    /// 用于钉住收尾的「句柄值匹配摘除」——传错句柄会让条目泄漏，此处可观测。
    /// </summary>
    internal bool HasActiveLoopForTest(string sessionId) => _activeLoops.ContainsKey(sessionId);

    /// <summary>测试用：预置一个活跃循环登记条目（构造「传错句柄」的确定性场景）。</summary>
    internal void RegisterActiveLoopForTest(string sessionId, LoopHandle handle) =>
        _activeLoops[sessionId] = handle.Loop;

    #endregion

    /// <summary>登记等待槽：key = 入站消息 Id。调用点保证消息尚未落盘，故不可能已被消费。</summary>
    private void RegisterAwaiter(string messageId, ExecutionAwaiter awaiter)
    {
        awaiter.MessageId = messageId;

        // 消息 Id 应全局唯一（由 Guid 生成）。若撞键说明有上层复用 Id，
        // 此时静默覆盖会悄悄丢弃另一个等待方，必须显式报错。
        if (!_awaiters.TryAdd(messageId, awaiter))
            throw new InvalidOperationException(
                $"等待槽登记冲突：消息 Id 已存在等待者（MessageId={messageId}）。");
    }

    /// <summary>撤销登记（幂等；已被消费取走时无操作）。</summary>
    private void RemoveAwaiter(ExecutionAwaiter awaiter)
    {
        if (awaiter.MessageId is { } messageId)
            _awaiters.TryRemove(messageId, out _);
    }

    /// <summary>
    /// 取走本批消息对应的等待槽。用「消息 Id 交集」而非当前执行推断，
    /// 因此即使执行体迟到（全局并发排队），也不会绑定到错误的轮次。
    /// </summary>
    private List<ExecutionAwaiter> TakeAwaitersFor(IReadOnlyList<SessionMessage> batch)
    {
        var taken = new List<ExecutionAwaiter>();
        foreach (var message in batch)
        {
            if (_awaiters.TryRemove(message.Id, out var awaiter))
                taken.Add(awaiter);
        }

        return taken;
    }

    /// <summary>
    /// 本轮进入终态：为等待者组装 <see cref="ExecutionOutcome"/> 并完成。
    /// <para>
    /// 会话已被 Stop 时改为取消等待：硬停不是「正常终态」，把它当成功返回会误导调用方。
    /// </para>
    /// </summary>
    private void TryCompleteAwaiters(
        string sessionId, ExecutionRecord record, List<ExecutionAwaiter> awaiters)
    {
        if (awaiters.Count == 0)
            return;

        if (_stoppedSessions.ContainsKey(sessionId) ||
            Volatile.Read(ref _disposed) != 0 ||
            _disposeCts.IsCancellationRequested)
        {
            CancelAwaiters(awaiters, new OperationCanceledException("会话已停止或运行时已释放，执行未产生可用终态。"));
            return;
        }

        var outcome = ExecutionOutcomeFactory.Create(
            record,
            _services,
            _logger,
            // 本通道口径：墙钟（含排队与工具等待）。
            // 事件通道的 outcome 用模型循环累计时长，两者刻意不同——见工厂类注释。
            duration: null);
        foreach (var awaiter in awaiters)
            awaiter.TrySetResult(outcome);
    }

    /// <summary>取消一组等待槽（执行未启动 / 会话被停止 / 运行时释放）。</summary>
    private static void CancelAwaiters(IEnumerable<ExecutionAwaiter> awaiters, Exception exception)
    {
        foreach (var awaiter in awaiters)
            CancelAwaiter(awaiter, exception);
    }

    /// <summary>取消单个等待槽：取消类异常走 SetCanceled，其余走 SetException。</summary>
    private static void CancelAwaiter(ExecutionAwaiter awaiter, Exception exception)
    {
        if (exception is OperationCanceledException oce)
            awaiter.TrySetCanceled(oce.CancellationToken);
        else
            awaiter.TrySetException(exception);
    }

    /// <summary>
    /// 取消某会话仍在等待中的等待槽。
    /// 覆盖「消息不会再被任何一轮执行消费」的情况（Stop / Dispose / 会话删除）——
    /// 此时不会再有 outcome 来兑现承诺。
    /// <para>
    /// <b>只取消已落盘（<see cref="ExecutionAwaiter.Persisted"/>）的等待槽</b>：
    /// 尚未落盘说明投递线程正握着消息、马上会进会话，此时取消属于误杀。
    /// 这类等待的兜底在投递侧：<c>DeliverAndAwaitAsync</c> 落盘后会复查
    /// <c>_stoppedSessions</c> 并主动抛出取消（那里没有「循环已退出」的歧义）。
    /// </para>
    /// <para>
    /// <b>不用于「无未读 → 循环退出」路径</b>：那一刻没有未读只是瞬时事实，
    /// 消息可能已被上一轮消费（等待已兑现），取消会误杀已兑现的等待。
    /// 该路径上「消息被第三方删除」的残留等待是已知边界，见
    /// <c>ISessionRuntime.DeliverAndAwaitAsync</c> 的 remarks。
    /// </para>
    /// </summary>
    private int CancelAwaitersForSession(string sessionId, Exception exception)
    {
        var cancelled = 0;
        foreach (var pair in _awaiters)
        {
            if (!string.Equals(pair.Value.SessionId, sessionId, StringComparison.Ordinal))
                continue;
            if (!pair.Value.Persisted)
                continue;
            if (!_awaiters.TryRemove(pair.Key, out var awaiter))
                continue;

            CancelAwaiter(awaiter, exception);
            cancelled++;
        }

        return cancelled;
    }

    /// <summary>取消全部等待槽（运行时释放）。</summary>
    private int CancelAllAwaiters(Exception exception)
    {
        var cancelled = 0;
        foreach (var key in _awaiters.Keys.ToArray())
        {
            if (_awaiters.TryRemove(key, out var awaiter))
            {
                CancelAwaiter(awaiter, exception);
                cancelled++;
            }
        }

        return cancelled;
    }

    /// <summary>按「值匹配」移除循环条目，避免误删重新注册的新循环。</summary>
    private void RemoveLoop(string sessionId, LoopHandle handle)
    {
        ((ICollection<KeyValuePair<string, Task>>)_activeLoops)
            .Remove(new KeyValuePair<string, Task>(sessionId, handle.Loop));
    }

    /// <summary>单个等待槽：一次 <c>DeliverAndAwaitAsync</c> 的终态承诺。</summary>
    private sealed class ExecutionAwaiter
    {
        public ExecutionAwaiter(string sessionId) => SessionId = sessionId;

        public string SessionId { get; }

        /// <summary>登记后由投递过程回填（消息构造完成、落盘之前）。</summary>
        public string? MessageId { get; set; }

        private int _persisted;

        /// <summary>
        /// 消息已落盘（投递成功返回）。
        /// <para>
        /// 停止清扫只作用于已落盘的等待槽：未落盘说明投递线程正握着消息、马上会进会话，
        /// 此时取消属于误杀。
        /// </para>
        /// </summary>
        public bool Persisted => Volatile.Read(ref _persisted) != 0;

        public TaskCompletionSource<ExecutionOutcome> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void MarkPersisted() => Volatile.Write(ref _persisted, 1);

        public void TrySetResult(ExecutionOutcome outcome) => Completion.TrySetResult(outcome);

        public void TrySetCanceled(CancellationToken token = default) => Completion.TrySetCanceled(token);

        public void TrySetException(Exception ex) => Completion.TrySetException(ex);
    }

    /// <summary>
    /// 循环句柄：Ready 门控保证「先登记后运行」；Loop 持自身 Task 供值匹配移除。
    /// <para>internal 供测试直接构造句柄并调用 <see cref="TeardownLoop"/>（覆盖真实收尾接线）。</para>
    /// </summary>
    internal sealed class LoopHandle
    {
        public TaskCompletionSource Ready { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Loop { get; set; } = Task.CompletedTask;
    }

    /// <summary>
    /// 该 presentation 是否「落盘即已消费」——即永远不会进入未读集合、不会触发执行。
    /// <para>
    /// 这是**唯一判定源**：<see cref="BuildInboxMessage"/> 用它决定 <c>consumed</c> 元数据，
    /// <see cref="DeliverAndAwaitAsync"/> 用它拒绝「永远等不到执行」的投递。
    /// 两处共用同一个方法，规则不会漂移。
    /// </para>
    /// </summary>
    private static bool IsBornConsumed(InboxPresentation presentation) =>
        presentation is InboxPresentation.Assistant or InboxPresentation.None;

    /// <summary>构造入站 <see cref="SessionMessage"/>（Presentation → Role 映射 + inbox.* 元数据）。</summary>
    private SessionMessage BuildInboxMessage(InboxMessage message, IServiceProvider scopeServices)
    {
        var presentation = message.Presentation switch
        {
            InboxPresentation.System => "system",
            InboxPresentation.Assistant => "assistant",
            InboxPresentation.None => "none",
            _ => "user"
        };

        string role;
        string content;

        switch (message.Presentation)
        {
            case InboxPresentation.System:
                role = MessageRole.User;
                var renderer = scopeServices.GetService<ISystemReminderRenderer>();
                if (renderer is not null)
                {
                    var source = string.IsNullOrWhiteSpace(message.SourceTag)
                        ? SystemReminder.Sources.Task
                        : message.SourceTag!;
                    var kind = string.IsNullOrWhiteSpace(message.Kind)
                        ? SystemReminder.Kinds.Completed
                        : message.Kind!;
                    content = renderer.Wrap(message.Body ?? string.Empty, source, kind);
                }
                else
                {
                    // 防御性降级：拿不到渲染器时退回裸文本 user 消息
                    content = message.Body ?? string.Empty;
                }
                break;

            case InboxPresentation.Assistant:
                // 外部产出写回：落盘即已消费，不参与未读、不唤醒
                role = MessageRole.Assistant;
                content = message.Body ?? string.Empty;
                break;

            case InboxPresentation.None:
                // 可持久化展示但不进 LLM 历史（如 Gateway 取消/错误横幅）：系统角色承载。
                role = MessageRole.System;
                content = message.Body ?? string.Empty;
                break;

            default: // User
                role = MessageRole.User;
                content = message.Body ?? string.Empty;
                break;
        }

        // 「生来已消费」与 presentation 的映射只在 IsBornConsumed 定义一次。
        var consumed = IsBornConsumed(message.Presentation);

        var metadata = ImmutableDictionary.CreateBuilder<string, object>(StringComparer.Ordinal);
        metadata[InboxMetadataKeys.Delivery] = message.Delivery == InboxDelivery.Wake ? "wake" : "inject";
        metadata[InboxMetadataKeys.Consumed] = consumed;
        metadata[InboxMetadataKeys.SenderKind] = message.Sender.Kind switch
        {
            InboxSenderKind.Human => "human",
            InboxSenderKind.Session => "session",
            _ => "system"
        };
        if (!string.IsNullOrEmpty(message.Sender.Id))
            metadata[InboxMetadataKeys.SenderId] = message.Sender.Id!;
        if (!string.IsNullOrEmpty(message.Sender.DisplayName))
            metadata[InboxMetadataKeys.SenderName] = message.Sender.DisplayName!;
        metadata[InboxMetadataKeys.Presentation] = presentation;
        if (!string.IsNullOrEmpty(message.SourceTag))
            metadata[InboxMetadataKeys.Source] = message.SourceTag!;
        if (!string.IsNullOrEmpty(message.Kind))
            metadata[InboxMetadataKeys.Kind] = message.Kind!;
        if (!string.IsNullOrEmpty(message.AgentId))
            metadata[InboxMetadataKeys.AgentId] = message.AgentId!;
        if (!string.IsNullOrEmpty(message.ModelId))
            metadata[InboxMetadataKeys.ModelId] = message.ModelId!;
        if (message.Conversation is not null)
        {
            metadata[InboxMetadataKeys.Conversation] =
                JsonSerializer.Serialize(message.Conversation, ConversationJsonOptions);
        }
        if (!string.IsNullOrEmpty(message.ThinkingEffort))
            metadata[InboxMetadataKeys.ThinkingEffort] = message.ThinkingEffort!;
        if (!string.IsNullOrEmpty(message.ModeId))
            metadata[InboxMetadataKeys.ModeId] = message.ModeId!;
        if (!string.IsNullOrEmpty(message.WorkingDirectory))
            metadata[InboxMetadataKeys.WorkingDirectory] = message.WorkingDirectory!;

        return new SessionMessage
        {
            Id = Guid.NewGuid().ToString("N"),
            Role = role,
            Content = content,
            Parts = BuildContentParts(content, message.Attachments),
            CreatedAt = DateTime.UtcNow,
            Metadata = metadata.ToImmutable()
        };
    }

    /// <summary>
    /// 构造消息内容段：有附件时转成多模态 Parts（文本 + image/file），否则返回 null 走纯文本。
    /// 对齐旧 <c>ExecutionRunner.BuildUserMessage</c> 的附件映射。
    /// </summary>
    private static ImmutableList<SessionContentPart>? BuildContentParts(
        string content,
        IReadOnlyList<ChatAttachment>? attachments)
    {
        if (attachments is not { Count: > 0 })
            return null;

        var parts = new List<SessionContentPart>();
        if (!string.IsNullOrWhiteSpace(content))
            parts.Add(SessionContentPart.CreateText(content));

        foreach (var att in attachments)
        {
            if (string.IsNullOrEmpty(att.Base64Data) && !string.IsNullOrEmpty(att.LocalPath))
            {
                // 引用式附件：只带路径，出站前由框架物化（会话不存 base64）
                if (att.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    parts.Add(SessionContentPart.CreateImageFromPath(att.LocalPath, att.MimeType, att.FileName));
                else
                    parts.Add(SessionContentPart.CreateFileFromPath(att.LocalPath, att.MimeType, att.FileName));

                continue;
            }

            if (att.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                parts.Add(SessionContentPart.CreateImageFromBase64(att.Base64Data, att.MimeType));
            else
                parts.Add(SessionContentPart.CreateFileFromBase64(att.Base64Data, att.MimeType, att.FileName));
        }

        return parts.ToImmutableList();
    }

    private void PublishNotice(InboxMessage message)
    {
        _publisher.Publish(message.TargetSessionId, new InboxNoticeEvent
        {
            SessionId = message.TargetSessionId,
            SenderKind = message.Sender.Kind switch
            {
                InboxSenderKind.Human => "human",
                InboxSenderKind.Session => "session",
                _ => "system"
            },
            SenderId = message.Sender.Id,
            SourceTag = message.SourceTag,
            Kind = message.Kind,
            Body = message.Body
        });
    }

    private static bool IsPendingInboxMessage(SessionMessage message)
    {
        if (message.Metadata is null)
            return false;

        var presentation = GetMetaString(message.Metadata, InboxMetadataKeys.Presentation);
        if (presentation is null)
            return false; // 非入站消息
        if (string.Equals(presentation, "assistant", StringComparison.OrdinalIgnoreCase))
            return false;

        return !(TryGetMetaBool(message.Metadata, InboxMetadataKeys.Consumed, out var consumed) && consumed);
    }

    private static int CountUnread(SessionData session) =>
        session.Messages.Count(IsPendingInboxMessage);

    private static string? GetMetaString(IReadOnlyDictionary<string, object>? metadata, string key)
    {
        if (metadata is null || !metadata.TryGetValue(key, out var raw) || raw is null)
            return null;

        return raw switch
        {
            string s => s,
            JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
            _ => raw.ToString()
        };
    }

    private static readonly JsonSerializerOptions ConversationJsonOptions = CreateConversationJsonOptions();

    private static JsonSerializerOptions CreateConversationJsonOptions()
    {
        var options = SeeingJson.Create(o =>
        {
            o.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            o.PropertyNameCaseInsensitive = true;
        });
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    /// <summary>将 inbox.conversation 的 ConversationInfo JSON 还原为会话路由。</summary>
    private static SessionChannelRoute? ParseConversationRoute(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            var info = JsonSerializer.Deserialize<ConversationInfo>(json, ConversationJsonOptions);
            return info?.ToSessionRoute();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>取本批消息中指定元数据的最后一个非空值（后到者覆盖，与「最新选择」语义一致）。</summary>
    private static string? LastNonEmptyMeta(IReadOnlyList<SessionMessage> messages, string key)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var value = GetMetaString(messages[i].Metadata, key);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static bool TryGetMetaBool(IReadOnlyDictionary<string, object>? metadata, string key, out bool value)
    {
        value = false;
        if (metadata is null || !metadata.TryGetValue(key, out var raw) || raw is null)
            return false;

        switch (raw)
        {
            case bool b:
                value = b;
                return true;
            case string s:
                return bool.TryParse(s, out value);
            case JsonElement je when je.ValueKind == JsonValueKind.True:
                value = true;
                return true;
            case JsonElement je when je.ValueKind == JsonValueKind.False:
                value = false;
                return true;
            default:
                return false;
        }
    }

    public Task<bool> InterruptAsync(string sessionId, CancellationToken ct = default)
    {
        // 停当前轮：取消当前执行体；consumed 不回退，循环继续消费剩余未读。
        // 不触碰 _stoppedSessions（Stop 与 Interrupt 的唯一区别在循环去向）。
        Touch(sessionId);
        var cts = _states.TryGetValue(sessionId, out var state) ? state.CurrentExecution?.Cts : null;
        if (cts is null)
            return Task.FromResult(false);

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 执行体已进入终态并释放令牌：视为无可中断目标。
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public async Task<int> DiscardPendingAsync(string sessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return 0;

        var gate = _sessionGates.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var scope = _services.CreateScope();
            var sessionManager = scope.ServiceProvider.GetRequiredService<ISessionManager>();
            var session = await sessionManager.TryGetOrLoadAsync(sessionId).ConfigureAwait(false);
            if (session is null)
                return 0;

            var pending = session.Messages.Where(IsPendingInboxMessage).ToList();
            if (pending.Count == 0)
                return 0;

            foreach (var message in pending)
            {
                var metadata = message.Metadata ?? ImmutableDictionary<string, object>.Empty;
                message.Metadata = metadata.SetItem(InboxMetadataKeys.Consumed, true);

                // 被丢弃的消息不会再有执行来兑现等待：显式取消而不是让调用方永久挂起。
                if (_awaiters.TryRemove(message.Id, out var awaiter))
                    CancelAwaiter(awaiter, new OperationCanceledException("入站消息已被丢弃，不会产生执行终态。"));
            }

            await sessionManager.SaveAsync(sessionId).ConfigureAwait(false);
            return pending.Count;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<int> StopAsync(string sessionId, CancellationToken ct = default)
    {
        // 级联范围：自身 + 整棵子树（父停止即放弃所有后代，不论子是否被人接管）。
        var targets = new List<string> { sessionId };
        try
        {
            var descendants = await CollectSubtreeAsync(sessionId, ct).ConfigureAwait(false);
            targets.AddRange(descendants);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "级联停止子会话枚举失败: SessionId={SessionId}", sessionId);
        }

        // 1. 先对子会话写 stop_reason 并落盘（先于加入 stopped 与取消，保证监听器读到时标记已可见）
        if (targets.Count > 1)
        {
            try
            {
                using var scope = _services.CreateScope();
                var sessionManager = scope.ServiceProvider.GetService<ISessionManager>();
                if (sessionManager is not null)
                {
                    foreach (var childId in targets.Skip(1))
                    {
                        try
                        {
                            var child = await sessionManager.TryGetOrLoadAsync(childId).ConfigureAwait(false);
                            if (child is null)
                                continue;

                            child.Metadata[SessionControlKeys.StopReason] = SessionControlKeys.StopReasonCascadedFromParent;
                            await sessionManager.SaveAsync(childId).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "写级联停止标记失败: ChildSessionId={ChildSessionId}", childId);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "级联停止标记会话解析失败: SessionId={SessionId}", sessionId);
            }
        }

        // 2. 加入停止态（自身 + 子会话）
        foreach (var id in targets)
            _stoppedSessions[id] = 0;

        // 2.5 取消仍在等待的终态承诺：硬停不会产生正常的 outcome，必须显式收口而不是永久挂起。
        foreach (var id in targets)
            CancelAwaitersForSession(id, new OperationCanceledException($"会话 {id} 已停止，执行未产生可用终态。"));

        // 3. 取消当前执行体
        var cancelled = 0;
        foreach (var id in targets)
        {
            Touch(id);
            var cts = _states.TryGetValue(id, out var state) ? state.CurrentExecution?.Cts : null;
            if (cts is null)
                continue;

            try
            {
                cts.Cancel();
                cancelled++;
            }
            catch (ObjectDisposedException)
            {
                // 已终态：不计入
            }
        }

        _logger.LogInformation(
            "会话已停止: SessionId={SessionId}, Cascaded={Cascaded}, Cancelled={Cancelled}",
            sessionId, targets.Count - 1, cancelled);
        return cancelled;
    }

    /// <summary>
    /// 递归收集 <paramref name="sessionId"/> 的整棵子树（所有后代 id，BFS）。
    /// 单次枚举失败不阻断其余分支；已访问集合防止环与重复。
    /// </summary>
    private async Task<List<string>> CollectSubtreeAsync(string sessionId, CancellationToken ct)
    {
        var descendants = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { sessionId };
        var queue = new Queue<string>();
        queue.Enqueue(sessionId);

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var current = queue.Dequeue();

            IReadOnlyList<SessionData> children;
            try
            {
                using var scope = _services.CreateScope();
                var groupManager = scope.ServiceProvider.GetService<ISessionGroupManager>();
                if (groupManager is null)
                    continue;

                children = await groupManager.ListChildrenAsync(current, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "级联停止子树枚举失败: SessionId={SessionId}", current);
                continue;
            }

            foreach (var child in children)
            {
                if (string.IsNullOrEmpty(child.Id) || !visited.Add(child.Id))
                    continue;

                descendants.Add(child.Id);
                queue.Enqueue(child.Id);
            }
        }

        return descendants;
    }

    public async Task<int> CancelAllInFlightAsync(CancellationToken ct = default)
    {
        // 对活跃循环快照逐个 Stop（进程级：热重载/关闭时强制取消消费）。
        var sessionIds = _activeLoops.Keys.ToArray();
        var cancelled = 0;
        foreach (var sessionId in sessionIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                cancelled += await StopAsync(sessionId, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "取消活跃会话失败: SessionId={SessionId}", sessionId);
            }
        }

        return cancelled;
    }

    public async Task WaitForIdleAsync(string sessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(sessionId))
            return;

        // Stop 态视为 idle：循环已（或即将）退出，直接返回。
        if (_stoppedSessions.ContainsKey(sessionId))
        {
            // 循环已（或即将）退出，不会有下一轮来兑现等待。
            CancelAwaitersForSession(sessionId, new OperationCanceledException("会话已停止，执行未产生可用终态。"));
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
        var token = linked.Token;
        var deadline = DateTime.UtcNow + WaitForIdleSafetyTimeout;

        while (true)
        {
            token.ThrowIfCancellationRequested();

            if (!_activeLoops.ContainsKey(sessionId))
            {
                // 循环未在跑：要么已 Stop（上面已返回），要么无未读 Wake → idle。
                if (_stoppedSessions.ContainsKey(sessionId) || !HasUnreadWake(sessionId))
                    return;
            }

            if (DateTime.UtcNow >= deadline)
            {
                _logger.LogWarning("WaitForIdleAsync 超时退出: SessionId={SessionId}", sessionId);
                return;
            }

            await Task.Delay(25, token).ConfigureAwait(false);
        }
    }

    public SessionExecutionOverview GetOverview(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId) || !_states.TryGetValue(sessionId, out var state))
            return new SessionExecutionOverview();

        // 未读数始终实时计算（不依赖 Deliver 时写入的缓存值）：
        // 消费循环标记 consumed 后未读数即下降，避免 UI 显示过期计数。
        var pending = CountUnreadForSession(sessionId);

        return new SessionExecutionOverview
        {
            CurrentExecution = state.CurrentExecution,
            LastExecution = state.LastExecution,
            PendingInputCount = pending
        };
    }

    public bool HasAnyActiveExecution() =>
        _states.Values.Any(s => s.CurrentExecution != null);

    public IReadOnlyList<IMessageEvent> GetBufferedEvents(string sessionId) =>
        _publisher.GetBufferedEvents(sessionId);

    public IAsyncEnumerable<IMessageEvent> SubscribeEvents(string sessionId, CancellationToken ct = default) =>
        _publisher.SubscribeAsync(sessionId, ct);

    /// <summary>
    /// 和解加载时残留的未完成工具调用：会话无活跃执行时，把 pending/running 收敛为 cancelled 并落盘。
    /// <para>
    /// 覆盖范围：task 卡片（含子会话仍活跃则跳过的判定）+ 普通工具调用。
    /// <c>Running</c> 会随事件落盘（刷新后正确显示「执行中」），因此进程崩溃/强制关闭后
    /// 必须在此收敛，否则会永久停留在执行中。
    /// </para>
    /// </summary>
    public async Task<int> ReconcileIncompleteTasksAsync(string sessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(sessionId))
            return 0;

        ct.ThrowIfCancellationRequested();

        // 会话自身仍在执行（如前台 task 阻塞父 Loop 期间刷新页面）：TaskId 尚未回填，
        // 不能把在途 task 误判为孤儿取消。
        if (GetOverview(sessionId).HasActiveExecution)
            return 0;

        using var scope = _services.CreateScope();
        var sessionManager = scope.ServiceProvider.GetRequiredService<ISessionManager>();

        var session = await sessionManager.TryGetOrLoadAsync(sessionId).ConfigureAwait(false);
        if (session?.Messages == null)
            return 0;

        var count = 0;
        foreach (var msg in session.Messages)
        {
            if (msg.ToolCalls == null)
                continue;

            foreach (var tc in msg.ToolCalls)
            {
                if (!IsIncompleteToolCall(tc))
                    continue;

                // 后台/前台 task：其子会话仍在执行时不得误判为孤儿（父会话可能已空闲）
                if (IsTaskToolCall(tc))
                {
                    var taskId = tc.TaskId;
                    var stillActive = !string.IsNullOrEmpty(taskId)
                        && GetOverview(taskId).HasActiveExecution;

                    if (stillActive)
                        continue;
                }

                tc.Status = "cancelled";
                tc.Error = IsTaskToolCall(tc)
                    ? "任务已中断（进程关闭或取消）"
                    : "工具调用已中断（进程关闭或取消）";
                count++;
            }
        }

        if (count > 0)
        {
            await sessionManager.SaveAsync(sessionId).ConfigureAwait(false);
            _logger.LogInformation(
                "Reconciled {Count} incomplete task card(s) for session {SessionId}", count, sessionId);
        }

        return count;
    }

    private static bool IsTaskToolCall(SessionToolCall tc)
        => !string.IsNullOrEmpty(tc.TaskId)
           || string.Equals(tc.Name, "task", StringComparison.OrdinalIgnoreCase);

    /// <summary>未完成（pending/running）的工具调用；无活跃执行时即孤儿，需收敛为已中断。</summary>
    private static bool IsIncompleteToolCall(SessionToolCall tc)
    {
        if (tc == null)
            return false;

        var status = tc.Status?.ToLowerInvariant();
        return status is "running" or "pending";
    }

    // IExecutionInFlightBoundary
    public bool HasInFlight() => HasAnyActiveExecution();

    /// <summary>
    /// 等到没有未终态执行为止。<see cref="CancelAllInFlightAsync"/> 只发出取消信号，
    /// 循环还要跑完收尾才会清掉 <c>CurrentExecution</c>；切换工作区必须等它真的落地。
    /// </summary>
    public async Task WaitUntilIdleAsync(CancellationToken cancellationToken = default)
    {
        while (HasAnyActiveExecution())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);   // 轮询间隔（E5）
        }
    }

    public IReadOnlyList<string> ListInFlightExecutionIds() =>
        _states.Values
            .Select(s => s.CurrentExecution?.ExecutionId)
            .Where(id => !string.IsNullOrEmpty(id))
            .Cast<string>()
            .ToArray();

    /// <summary>记录会话最近活动时间（供 CleanupIdleSessions 空闲回收）。</summary>
    private void Touch(string sessionId) => _lastActive[sessionId] = DateTime.UtcNow;

    /// <summary>会话是否存在未读 Wake 输入（consumed=false && delivery=wake）。</summary>
    private bool HasUnreadWake(string sessionId)
    {
        try
        {
            using var scope = _services.CreateScope();
            var sessionManager = scope.ServiceProvider.GetService<ISessionManager>();
            var session = sessionManager?.Get(sessionId);
            if (session is null)
                return false;

            return session.Messages.Any(m =>
                IsPendingInboxMessage(m)
                && string.Equals(GetMetaString(m.Metadata, InboxMetadataKeys.Delivery),
                    "wake", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>实时计算会话未消费输入数（不含外部产出写回）；会话不可用时返回 0。</summary>
    private int CountUnreadForSession(string sessionId)
    {
        try
        {
            using var scope = _services.CreateScope();
            var sessionManager = scope.ServiceProvider.GetService<ISessionManager>();
            var session = sessionManager?.Get(sessionId);
            return session is null ? 0 : CountUnread(session);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 定时回收空闲会话的运行态：超过 <see cref="ExecutionOptions.SessionIdleTimeout"/> 无活动
    /// 且无在途执行时，清理 <c>_states</c>/<c>_sessionGates</c>/<c>_stoppedSessions</c>。
    /// </summary>
    internal void CleanupIdleSessions()
    {
        var cutoff = DateTime.UtcNow - _options.SessionIdleTimeout;
        foreach (var (sessionId, lastActive) in _lastActive.ToArray())
        {
            if (lastActive > cutoff)
                continue;
            if (_activeLoops.ContainsKey(sessionId))
                continue;
            if (_states.TryGetValue(sessionId, out var state) && state.CurrentExecution is not null)
                continue;

            _lastActive.TryRemove(sessionId, out _);
            _states.TryRemove(sessionId, out _);
            _stoppedSessions.TryRemove(sessionId, out _);

            if (_sessionGates.TryRemove(sessionId, out var gate))
                gate.Dispose();

            _logger.LogDebug("回收空闲会话运行态: SessionId={SessionId}", sessionId);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _cleanupTimer?.Dispose();

        // 运行时释放：在途等待不可能再有结果，先显式取消字典里还在的等待槽。
        CancelAllAwaiters(new OperationCanceledException("会话运行时已释放，未完成的等待被取消。"));

        // 同时取消在途执行的令牌：否则执行体可能继续跑到自然结束，
        // 而它的等待槽已被本轮取走（不在字典里），永远收不到取消信号。
        foreach (var state in _states.Values)
        {
            try
            {
                state.CurrentExecution?.Cts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 已终态并释放令牌：无需处理。
            }
        }

        try
        {
            _disposeCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // ignore
        }
        _disposeCts.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
