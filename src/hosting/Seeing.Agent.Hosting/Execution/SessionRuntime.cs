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

                    // Stop 二次校验：StopAsync 可能落在「终止检查通过后、标记 consumed 前」。
                    // 必须在标记 consumed 之前判定：命中则退出且不消费本批，避免消息被静默丢失。
                    if (_stoppedSessions.ContainsKey(sessionId))
                    {
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

                ExecutionRecord? record = null;
                try
                {
                    await _globalConcurrency.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        // Stop 可能落在「全局并发等待期间」——此时 record 尚未创建，取消 record.Cts 无从生效。
                        // 放行后、创建 record 前再次校验停止态；命中则本轮不执行，退出循环。
                        if (_stoppedSessions.ContainsKey(sessionId) || ct.IsCancellationRequested)
                            break;

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

                        await _runner.RunOnceAsync(record, batch, record.Cts.Token).ConfigureAwait(false);
                        state.LastExecution = record;
                    }
                    finally
                    {
                        _states.GetOrAdd(sessionId, _ => new SessionExecutionState()).CurrentExecution = null;
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

            case InboxPresentation.None:
                // 可持久化展示但不进 LLM 历史（如 Gateway 取消/错误横幅）：
                // 系统角色承载，落盘即已消费，不参与未读、不唤醒。
                role = MessageRole.System;
                content = message.Body ?? string.Empty;
                consumed = true;
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
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
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
                            var child = sessionManager.Get(childId) ?? await sessionManager.LoadAsync(childId).ConfigureAwait(false);
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
            return;

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

        var session = sessionManager.Get(sessionId) ?? await sessionManager.LoadAsync(sessionId).ConfigureAwait(false);
        if (session?.Messages == null)
            return 0;

        var count = 0;
        foreach (var msg in session.Messages)
        {
            if (msg.ToolCalls == null)
                continue;

            foreach (var tc in msg.ToolCalls)
            {
                if (!IsIncompleteTaskToolCall(tc))
                    continue;

                var taskId = tc.TaskId;
                var stillActive = !string.IsNullOrEmpty(taskId)
                    && GetOverview(taskId).HasActiveExecution;

                if (stillActive)
                    continue;

                tc.Status = "cancelled";
                tc.Error = "任务已中断（进程关闭或取消）";
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

    private static bool IsIncompleteTaskToolCall(SessionToolCall tc)
    {
        if (tc == null)
            return false;

        var isTask = !string.IsNullOrEmpty(tc.TaskId)
            || string.Equals(tc.Name, "task", StringComparison.OrdinalIgnoreCase);
        if (!isTask)
            return false;

        var status = tc.Status?.ToLowerInvariant();
        return status is "running" or "pending";
    }

    // IExecutionInFlightBoundary
    public bool HasInFlight() => HasAnyActiveExecution();

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
