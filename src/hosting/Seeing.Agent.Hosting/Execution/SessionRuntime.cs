using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Reminders;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Agent.Abstractions.Sessions;
using Seeing.Agent.Core.Execution;
using Seeing.Session.Core;

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
    private readonly ConcurrentDictionary<string, ExecutionRecord> _executions = new();
    private readonly SemaphoreSlim _globalConcurrency;
    private readonly IExecutionRunner _runner;
    private readonly IExecutionEventPublisher _publisher;
    private readonly IServiceProvider _services;
    private readonly ExecutionOptions _options;
    private readonly ILogger<SessionRuntime> _logger;
    private readonly CancellationTokenSource _disposeCts = new();

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
    }

    public async Task<InboxResult> DeliverAsync(InboxMessage message, CancellationToken ct = default)
    {
        if (message is null)
            return InboxResult.Failed("InboxMessage is required");
        if (string.IsNullOrWhiteSpace(message.TargetSessionId))
            return InboxResult.Failed("TargetSessionId is required");

        var sessionId = message.TargetSessionId;

        using var scope = _services.CreateScope();
        var sessionManager = scope.ServiceProvider.GetRequiredService<ISessionManager>();

        // 1. 解析目标会话（不存在 → Failed）
        SessionData session;
        try
        {
            session = await sessionManager.GetOrLoadAsync(sessionId, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return InboxResult.Failed($"Session not found: {sessionId}");
        }

        // 2. 鉴权：跨组鉴权在 Task 3.3 的工具层（session_inbox_send 走 tool.execute 门）实现；
        //    运行时入口暂直接放行（与 spec §11「缺失授权器则放行」一致）。
        // TODO(Task 3.3): 跨组投递经 IPermissionAuthorizerFactory 授权。

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
        string? persistedMessageId = null;
        if (message.Presentation != InboxPresentation.None)
        {
            var persisted = BuildInboxMessage(message, scope.ServiceProvider);
            persistedMessageId = persisted.Id;
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
                // 只出 UI 通知，不唤醒（Presentation=None 时亦不落盘，见步骤 4）
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
                        // 句柄持有自身 Task 引用，退出时按值匹配移除，避免误删「新循环」条目。
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
                        RemoveLoop(sessionId, handle);
                        break;
                    }

                    using var scope = _services.CreateScope();
                    var sessionManager = scope.ServiceProvider.GetRequiredService<ISessionManager>();
                    var session = sessionManager.Get(sessionId);
                    if (session is null)
                    {
                        RemoveLoop(sessionId, handle);
                        break;
                    }

                    pending = session.Messages.Where(IsPendingInboxMessage).ToList();
                    if (pending.Count == 0)
                    {
                        // 退出与检查在同一 gate 临界区内，无丢唤醒窗口
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

                    // Stop 二次校验：StopAsync 可能落在「终止检查通过后、执行体启动前」
                    if (_stoppedSessions.ContainsKey(sessionId))
                    {
                        RemoveLoop(sessionId, handle);
                        break;
                    }
                }
                finally
                {
                    gate.Release();
                }

                ExecutionRecord? record = null;
                try
                {
                    await _globalConcurrency.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
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

                        var hasUserInput = pending.Any(m =>
                            string.Equals(GetMetaString(m.Metadata, InboxMetadataKeys.Presentation),
                                "user", StringComparison.OrdinalIgnoreCase));
                        var firstUserText = pending
                            .FirstOrDefault(m => string.Equals(
                                GetMetaString(m.Metadata, InboxMetadataKeys.Presentation),
                                "user", StringComparison.OrdinalIgnoreCase))
                            ?.Content;
                        var batch = new InboxBatch(pending, hasUserInput, firstUserText);

                        await _runner.RunOnceAsync(record, batch, record.Cts.Token).ConfigureAwait(false);
                        state.LastExecution = record;
                    }
                    finally
                    {
                        _states.GetOrAdd(sessionId, _ => new SessionExecutionState()).CurrentExecution = null;
                        _globalConcurrency.Release();
                    }
                }
                catch (OperationCanceledException)
                {
                    // Interrupt/Stop 取消：本执行已终态；consumed 不回退。
                    // Interrupt → 循环继续消费剩余未读；Stop → 下轮终止检查命中退出。
                    if (record is not null)
                        _states.GetOrAdd(sessionId, _ => new SessionExecutionState()).LastExecution = record;
                }
                catch (Exception ex)
                {
                    // 执行失败：记录失败，继续循环消费剩余未读（避免整循环崩溃）
                    _logger.LogError(ex, "消费循环执行失败: SessionId={SessionId}", sessionId);
                    if (record is not null)
                        _states.GetOrAdd(sessionId, _ => new SessionExecutionState()).LastExecution = record;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 运行时释放取消循环等待
        }
        finally
        {
            RemoveLoop(sessionId, handle);
        }
    }

    /// <summary>按「值匹配」移除循环条目，避免误删重新注册的新循环。</summary>
    private void RemoveLoop(string sessionId, LoopHandle handle)
    {
        ((ICollection<KeyValuePair<string, Task>>)_activeLoops)
            .Remove(new KeyValuePair<string, Task>(sessionId, handle.Loop));
    }

    /// <summary>循环句柄：Ready 门控保证「先登记后运行」；Loop 持自身 Task 供值匹配移除。</summary>
    private sealed class LoopHandle
    {
        public TaskCompletionSource Ready { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Loop { get; set; } = Task.CompletedTask;
    }

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
        var consumed = false;

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
                role = MessageRole.Assistant;
                content = message.Body ?? string.Empty;
                consumed = true; // 外部产出写回：落盘即已消费，不参与未读、不唤醒
                break;

            default: // User
                role = MessageRole.User;
                content = message.Body ?? string.Empty;
                break;
        }

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

        return new SessionMessage
        {
            Id = Guid.NewGuid().ToString("N"),
            Role = role,
            Content = content,
            CreatedAt = DateTime.UtcNow,
            Metadata = metadata.ToImmutable()
        };
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

    public Task<bool> InterruptAsync(string sessionId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<int> StopAsync(string sessionId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<int> CancelAllInFlightAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task WaitForIdleAsync(string sessionId, CancellationToken ct = default) => throw new NotImplementedException();
    public SessionExecutionOverview GetOverview(string sessionId) => throw new NotImplementedException();
    public bool HasAnyActiveExecution() => throw new NotImplementedException();
    public IReadOnlyList<IMessageEvent> GetBufferedEvents(string sessionId) => throw new NotImplementedException();
    public IAsyncEnumerable<IMessageEvent> SubscribeEvents(string sessionId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<int> ReconcileIncompleteTasksAsync(string sessionId, CancellationToken ct = default) => throw new NotImplementedException();

    // IExecutionInFlightBoundary
    public bool HasInFlight() => throw new NotImplementedException();
    public IReadOnlyList<string> ListInFlightExecutionIds() => throw new NotImplementedException();

    public void Dispose()
    {
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
