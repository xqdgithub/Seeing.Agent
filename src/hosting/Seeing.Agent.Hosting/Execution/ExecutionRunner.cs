using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Models;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Prompts;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Commands;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Events;
using Seeing.Agent.Core.Execution;
using Seeing.Agent.Core.Instructions;
using Seeing.Agent.Core.Modules;
using Seeing.Agent.Core.Scenarios;
using Seeing.Agent.Core.Services;
using Seeing.Agent.Llm;
using Seeing.Session.Core;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Seeing.Agent.Hosting.Execution;

/// <summary>本批输入摘要：供标题门控与历史构建。</summary>
internal sealed record InboxBatch(
    IReadOnlyList<SessionMessage> Inputs,
    bool HasUserInput,
    string? FirstUserText,
    string? FirstUserMessageId = null);

/// <summary>跑一轮执行的端口（供 SessionRuntime 注入与测试 mock）。</summary>
internal interface IExecutionRunner
{
    Task<ExecutionRecord> RunOnceAsync(ExecutionRecord record, InboxBatch batch, CancellationToken executionToken);
}

/// <summary>
/// 跑一轮执行：模型/模式落盘、指令注入、投影事件、终态落盘，返回终态记录。
/// <para>
/// 不做忙闲/循环/队列管理——这些由 <c>SessionRuntime</c> 的消费循环负责。
/// </para>
/// </summary>
internal sealed class ExecutionRunner : IExecutionRunner
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IExecutionEventPublisher _eventPublisher;
    private readonly ExecutionOptions _options;
    private readonly IConfigSectionStore _configStore;
    private readonly CompactionRunner _compactionRunner;
    private readonly ILogger<ExecutionRunner> _logger;
    private readonly ISessionToolSchemaResolver _schemaResolver;

    public ExecutionRunner(
        IServiceProvider serviceProvider,
        IExecutionEventPublisher eventPublisher,
        ExecutionOptions options,
        IConfigSectionStore configStore,
        CompactionRunner compactionRunner,
        ILogger<ExecutionRunner> logger,
        ISessionToolSchemaResolver schemaResolver)
    {
        _serviceProvider = serviceProvider;
        _eventPublisher = eventPublisher;
        _options = options ?? new ExecutionOptions();
        _configStore = configStore;
        _compactionRunner = compactionRunner ?? throw new ArgumentNullException(nameof(compactionRunner));
        _logger = logger;
        // schema 唯一计算点经端口 ISessionToolSchemaResolver：必需注入，缺失即构造期失败，
        // 不再自构造回退实例，杜绝第二套结算路径与类型耦合。
        _schemaResolver = schemaResolver ?? throw new ArgumentNullException(nameof(schemaResolver));
    }

    /// <summary>跑一轮执行：模型/模式落盘、指令注入、投影事件、终态落盘，返回终态记录。不做忙闲/循环管理。</summary>
    public async Task<ExecutionRecord> RunOnceAsync(
        ExecutionRecord record,
        InboxBatch batch,
        CancellationToken executionToken)
    {
        // Create scope for this execution
        using var scope = _serviceProvider.CreateScope();
        var sessionManager = scope.ServiceProvider.GetRequiredService<ISessionManager>();
        var agentRegistry = scope.ServiceProvider.GetRequiredService<IAgentRegistry>();
        var executionRouter = scope.ServiceProvider.GetRequiredService<IAgentExecutor>();
        var agentSelectionResolver = scope.ServiceProvider.GetRequiredService<AgentSelectionResolver>();
        var modelManager = scope.ServiceProvider.GetRequiredService<IModelManager>();
        var workspaceProvider = scope.ServiceProvider.GetRequiredService<IWorkspaceProvider>();
        var executionWorld = scope.ServiceProvider.GetRequiredService<IExecutionWorld>();
        var commandRegistry = scope.ServiceProvider.GetRequiredService<ICommandRegistry>();
        var instructionManager = scope.ServiceProvider.GetRequiredService<IInstructionManager>();

        try
        {
            // 会话确保（失败则本轮失败：与旧 ProcessExecutionAsync 一致）
            var session = await sessionManager.EnsureSessionAsync(
                record.SessionId,
                scenario: scope.ServiceProvider.GetService<IDefaultWorkModeProvider>()?.GetDefaultScenario());

            try
            {
                // === 执行前职责（原 SubmitAsync）：出站回填、模型/模式/思考强度落盘、cwd、指令注入 ===
                // 新模型：入站消息已由 Deliver 落盘，本轮不再落盘 user 消息（SkipUserMessagePersist 语义恒真），
                // 故这些副作用失败一律容忍（与旧 SkipUserMessagePersist==true 分支一致，仅告警不中断本轮）。
                TryBackfillChannelRoute(session, record.Options?.ChannelRoute);

                // ⭐ Persist model/mode selection to session (ensures they're saved even if execution fails)
                ApplyInboundModelAndMode(session, record.Options?.ModelId, record.Options?.ModeId, modelManager);
                if (record.Options?.ThinkingEffort is not null)
                {
                    session.SelectedThinkingEffort = record.Options.ThinkingEffort.Trim();
                    session.UpdatedAt = DateTime.Now;
                }

                var cwd = record.Options?.WorkingDirectory
                    ?? session.WorkingDirectory
                    ?? executionWorld.Cwd;
                if (!string.Equals(session.WorkingDirectory, cwd, StringComparison.Ordinal))
                {
                    session.WorkingDirectory = cwd;
                }

                if (record.Options?.SkipInstructionInject != true)
                {
                    try
                    {
                        await instructionManager.InjectIfNeededAsync(
                            session,
                            cwd,
                            workspaceProvider.GetProjectRoot(),
                            CancellationToken.None,
                            batch.FirstUserMessageId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(
                            ex,
                            "Failed to inject project instructions for execution {ExecutionId}",
                            record.ExecutionId);
                    }
                }

                await sessionManager.SaveAsync(record.SessionId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist pre-execution state for execution {ExecutionId}", record.ExecutionId);
            }

            _logger.LogInformation("Execution {ExecutionId} started", record.ExecutionId);

            // Publish execution started event
            _eventPublisher.Publish(record.SessionId, new ExecutionStartedEvent
            {
                SessionId = record.SessionId,
                ExecutionId = record.ExecutionId
            });

            // 自动压缩门控：TokenBudget 标记 + 配置开启时，每轮 Agent 循环开始前触发压缩
            // GetSection 契约应返回非 null；防御性 ?. 避免测试/错误 mock 返回 null 时整轮执行 NRE 短路
            var autoCompaction = _configStore.GetSection<TokenBudgetAutoCompactionPeek>("TokenBudget")
                ?.AutoCompactionEnabled == true;
            if (autoCompaction &&
                session.PendingCompaction &&
                session.Messages.Count > 0)
            {
                // 统一入口：Started 先于摘要 Delta 发布，UI 进度时序正确
                var compactionOutcome = await _compactionRunner.RunAsync(session.Id, reason: "auto");
                // 失败防抖：不保留标记，避免每轮循环反复触发耗时的压缩尝试拖慢对话
                if (!compactionOutcome.Success)
                {
                    _logger.LogWarning("自动压缩失败，跳过本轮重试: {SessionId} {Error}",
                        session.Id, compactionOutcome.ErrorMessage);
                }
                session.PendingCompaction = false;
            }

            // Build execution context with background permission channel
            // 会话级结算 + schema 在此快照；本轮中途改 session.Scenario 不影响已算结果
            var context = await BuildExecutionContextAsync(
                session, record, agentRegistry, agentSelectionResolver, modelManager, workspaceProvider, executionWorld,
                scope.ServiceProvider);

            // 旁路生成标题（不阻塞主对话；命令不生成标题）
            // 标题门控按「本批含 Presentation=User 输入」：纯系统/子会话通知批次不生成标题
            if (batch.HasUserInput && !IsCommandInput(record.Input?.Text))
            {
                var titleSource = ResolveTitleSourceText(record.Input);
                if (!string.IsNullOrWhiteSpace(titleSource))
                {
                    var titleEnsuring = scope.ServiceProvider.GetRequiredService<ISessionTitleService>();
                    var fallbackModel = context.RequestModelId ?? session.SelectedModel;
                    _ = EnsureTitleFireAndForgetAsync(
                        titleEnsuring,
                        record.SessionId,
                        titleSource,
                        fallbackModel);
                }
            }

            // Process command if applicable
            var inputText = record.Input?.Text;
            // 命令要求结束本轮（shouldContinue=false / shouldExit）时短路，不再执行 Agent：
            // 避免把命令文本作为普通消息发送给大模型（如 /compact 后仍发起 LLM 请求导致失败）
            var commandConsumedExecution = false;
            if (IsCommandInput(inputText))
            {
                CommandResultEvent? lastCommandEvent = null;
                await foreach (var cmdEvent in ProcessCommandAsync(
                    record.SessionId, inputText, session, context, commandRegistry, executionToken))
                {
                    if (cmdEvent != null)
                    {
                        _eventPublisher.Publish(record.SessionId, cmdEvent);
                        // 短路判定仅看命令结果事件（ProcessCommandAsync 只 yield 命令结果或 null）
                        if (cmdEvent is CommandResultEvent commandResult)
                        {
                            lastCommandEvent = commandResult;
                        }
                    }
                }
                commandConsumedExecution = lastCommandEvent != null && !lastCommandEvent.ShouldContinue;
            }

            if (!commandConsumedExecution)
            {
                // Build history
                var messages = BuildHistoryFromSession(session);

                // 服务端负责将事件投影到 Session 并落盘；UI 只订阅展示
                var eventTracker = new ChatEventTracker();

                // schema 快照事件：与随后 ChatRequest.Tools 同源（context.ToolSchemas）
                var schemaSnapshot = new SchemaSnapshotEvent
                {
                    SessionId = record.SessionId,
                    ExecutionId = record.ExecutionId,
                    ToolIds = context.ToolSchemas?
                        .Where(s => s.Function != null)
                        .Select(s => s.Function.Name)
                        .ToArray()
                        ?? Array.Empty<string>(),
                    SectionIds = context.SectionIds,
                    ExposureMode = context.ExposureMode,
                    InlineToolCount = context.InlineToolCount,
                    DeferredToolCount = context.DeferredToolCount,
                    EstimatedDefinitionTokens = context.EstimatedDefinitionTokens,
                    CatalogTokens = context.CatalogTokens
                };
                eventTracker.ApplyEvent(session, schemaSnapshot);
                _eventPublisher.Publish(record.SessionId, schemaSnapshot);
                if (ShouldPersistEvent(schemaSnapshot))
                    await sessionManager.SaveAsync(record.SessionId);

                // Execute agent
                // 取消不再主动 throw：执行器（事件流水线）会产出终态事件（工具 Cancelled / LoopCancelledEvent），
                // 这些事件必须在取消后仍被处理与发布。OCE 由下方 catch 兜底（执行器未转换为事件的极端情况）。
                // 令牌已在本方法入参绑定本记录（executionToken），BuildAgentContext 与 ExecuteAsync 共用同一令牌。
                var loopFailed = false;
                string? loopError = null;
                await foreach (var evt in executionRouter.ExecuteAsync(
                    context.Agent,
                    messages,
                    BuildAgentContext(context, executionToken),
                    executionToken))
                {
                    // 事件流语义：LLM/Agent 失败以 ErrorEvent + LoopCompleteEvent(Success=false) 表达（不抛异常）
                    // 若不在此捕获，下方正常结束路径会把 record 标记为 Completed，导致 task_status 无法判定失败。
                    if (evt is ErrorEvent err)
                    {
                        loopFailed = true;
                        loopError = err.Message;
                    }
                    else if (evt is LoopCompleteEvent loopEnd && !loopEnd.Success)
                    {
                        loopFailed = true;
                        loopError ??= loopEnd.Error;
                    }

                    var liveSession = sessionManager.Get(record.SessionId) ?? session;
                    eventTracker.ApplyEvent(liveSession, evt);

                    // 先投影再发布，保证 UI 读到的是已写入的 SessionData
                    _eventPublisher.Publish(record.SessionId, evt);

                    if (ShouldPersistEvent(evt))
                        await sessionManager.SaveAsync(record.SessionId);
                }

                if (loopFailed)
                {
                    record.Status = ExecutionStatus.Failed;
                    record.ErrorMessage = loopError;
                }
            }

            // 执行器以终态事件正常结束时：若本执行已被取消（Interrupt/Stop 取消令牌）或
            // 事件流已标记 Failed，保留对应终态；否则标记完成。
            if (record.Status != ExecutionStatus.Cancelled &&
                record.Status != ExecutionStatus.Failed &&
                executionToken.IsCancellationRequested)
                record.Status = ExecutionStatus.Cancelled;
            else if (record.Status != ExecutionStatus.Cancelled &&
                     record.Status != ExecutionStatus.Failed)
                record.Status = ExecutionStatus.Completed;

            // 事件流失败（非异常）或 catch 异常失败：统一标记会话 Error 状态。
            // 执行失败后执行记录很快进入终态，仅靠执行记录无法长期判定失败，
            // task_status 回落判定依赖 SessionStatus。
            if (record.Status == ExecutionStatus.Failed)
                MarkSessionError(sessionManager, record.SessionId, record.ErrorMessage);
            else if (record.Status == ExecutionStatus.Completed)
                ClearSessionError(sessionManager, record.SessionId);

            _logger.LogInformation("Execution {ExecutionId} completed with status {Status}", record.ExecutionId, record.Status);
        }
        catch (OperationCanceledException)
        {
            record.Status = ExecutionStatus.Cancelled;
            _logger.LogInformation("Execution {ExecutionId} was cancelled", record.ExecutionId);

            try
            {
                var liveSession = sessionManager.Get(record.SessionId);
                if (IncompleteToolCallMarker.MarkCancelled(liveSession, "用户取消") > 0)
                    await sessionManager.SaveAsync(record.SessionId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "取消后标记未完成 Task 失败: {ExecutionId}", record.ExecutionId);
            }
        }
        catch (Exception ex)
        {
            record.Status = ExecutionStatus.Failed;
            record.ErrorMessage = ex.Message;
            _logger.LogError(ex, "Execution {ExecutionId} failed", record.ExecutionId);

            // 标记会话 Error 状态：task_status 回落判定依赖 SessionStatus
            MarkSessionError(sessionManager, record.SessionId, record.ErrorMessage);

            // 失败路径兜底：标记未走到终态的工具调用（pending/running）为 cancelled，
            // 避免异常退出后遗留孤儿的"运行中"工具状态（对齐 OperationCanceledException 路径）。
            try
            {
                var liveSession = sessionManager.Get(record.SessionId);
                if (IncompleteToolCallMarker.MarkCancelled(liveSession, $"执行失败：{ex.Message}") > 0)
                    await sessionManager.SaveAsync(record.SessionId);
            }
            catch (Exception markerEx)
            {
                _logger.LogWarning(markerEx, "失败后标记未完成工具调用异常: {ExecutionId}", record.ExecutionId);
            }

            // Publish error event
            _eventPublisher.Publish(record.SessionId, new ErrorEvent
            {
                SessionId = record.SessionId,
                Message = ex.Message
            });
        }
        finally
        {
            record.CompletedAt = DateTime.UtcNow;

            // Final save（先写 history 再落盘，避免 history 永远落后一次）
            try
            {
                await AppendExecutionHistoryAsync(sessionManager, record);
                await sessionManager.SaveAsync(record.SessionId);
                // 执行终态落盘：flush 确保最终状态持久化
                await sessionManager.FlushAsync(record.SessionId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save final state for execution {ExecutionId}", record.ExecutionId);
            }

            // 发布执行终态事件：UI/TUI 据此结束「执行中」态并刷新。
            // 每轮执行（含消费循环的每一轮）恰发布一次。
            _eventPublisher.Publish(record.SessionId, new ExecutionCompleteEvent
            {
                SessionId = record.SessionId,
                ExecutionId = record.ExecutionId,
                Status = record.Status
            });
        }

        return record;
    }

    /// <summary>
    /// Builds the execution context for an execution.
    /// </summary>
    private async Task<ChatExecutionContext> BuildExecutionContextAsync(
        SessionData session,
        ExecutionRecord record,
        IAgentRegistry agentRegistry,
        AgentSelectionResolver agentSelectionResolver,
        IModelManager modelManager,
        IWorkspaceProvider workspaceProvider,
        IExecutionWorld executionWorld,
        IServiceProvider services)
    {
        var agentId = await agentSelectionResolver.ResolveAgentIdAsync(
            record.Options?.AgentId,
            session.SelectedAgent,
            CancellationToken.None).ConfigureAwait(false);

        var agentDef = await agentRegistry.GetAgentAsync(agentId)
            ?? throw new InvalidOperationException($"Agent '{agentId}' not found");

        if (agentDef.Disabled)
            throw new InvalidOperationException($"Agent '{agentId}' is disabled");

        // 执行级权限授权器：经 IPermissionAuthorizerFactory 构造，
        // 覆盖值来自 ChatOptions.AutoApprove（null=FollowGlobal）；实时生效策略由授权器/引擎解析。
        var permissionAuthorizer = ResolvePermissionAuthorizer(
            services, record.SessionId, record.Options?.AutoApprove);

        var sessionModelRef = modelManager.GetSessionModelRef(session);
        var sessionModelRefOrNull = string.IsNullOrEmpty(sessionModelRef) ? null : sessionModelRef;

        string? requestModelId = agentDef.Runtime == AgentRuntime.AcpPassthrough
            ? modelManager.ResolveAcpModel(record.Options?.ModelId, sessionModelRefOrNull)
            : modelManager.ResolveNativeModel(record.Options?.ModelId, sessionModelRefOrNull, agentId);

        var acpModeId = agentSelectionResolver.ResolveAcpModeId(
            record.Options?.ModeId,
            session.SelectedAcpMode);

        var projectRoot = workspaceProvider.GetProjectRoot();
        var cwd = record.Options?.WorkingDirectory
            ?? session.WorkingDirectory
            ?? executionWorld.Cwd;

        // schema 唯一计算点：经 ISessionToolSchemaResolver 端口（与 Scheduler 直连执行共用同一结算出口）
        var schemaSettlement = await _schemaResolver.ResolveAsync(
            session, agentDef).ConfigureAwait(false);
        var toolSchemas = schemaSettlement.ToolSchemas;
        var sectionIds = schemaSettlement.SectionIds;

        // token 归属与目录正文均由 resolver 单点产出（含写入 session.CachedSchemaTokens/CatalogTokens），
        // 执行链只读不重算，主链与 Scheduler 旁路同获同源产物。

        string? requestThinkingEffort = record.Options?.ThinkingEffort;
        if (requestThinkingEffort is null && !string.IsNullOrWhiteSpace(session.SelectedThinkingEffort))
            requestThinkingEffort = session.SelectedThinkingEffort;

        return new ChatExecutionContext
        {
            SessionId = record.SessionId,
            Agent = agentDef,
            History = new List<ChatMessage>(),
            WorkingDirectory = cwd,
            WorkspaceRoot = projectRoot,
            PermissionAuthorizer = permissionAuthorizer,
            ChannelRoute = record.Options?.ChannelRoute,
            AcpModeId = acpModeId,
            RequestModelId = requestModelId,
            RequestThinkingEffort = requestThinkingEffort,
            ToolSchemas = toolSchemas,
            SectionIds = sectionIds,
            InlineToolCount = schemaSettlement.InlineToolCount,
            DeferredToolCount = schemaSettlement.DeferredToolCount,
            EstimatedDefinitionTokens = schemaSettlement.EstimatedDefinitionTokens,
            CatalogTokens = schemaSettlement.CatalogTokens,
            ExposureMode = schemaSettlement.ExposureMode,
            CatalogText = schemaSettlement.CatalogText
        };
    }

    /// <summary>
    /// 构造本执行级权限授权器（经 <see cref="IPermissionAuthorizerFactory"/>）。
    /// <para>
    /// <paramref name="autoApprove"/> 为执行级冻结覆盖（来自 <c>ChatOptions.AutoApprove</c>）；
    /// null 表示 FollowGlobal，由授权器/引擎实时解析会话三态与全局开关。
    /// </para>
    /// </summary>
    internal IPermissionAuthorizer? ResolvePermissionAuthorizer(
        IServiceProvider services,
        string sessionId,
        SessionAutoApprove? autoApprove)
    {
        var factory = services.GetService<IPermissionAuthorizerFactory>();
        if (factory is null)
        {
            _logger.LogWarning(
                "IPermissionAuthorizerFactory 未注册，本次执行无权限授权器: SessionId={SessionId}",
                sessionId);
            return null;
        }

        return factory.Create(sessionId, autoApprove);
    }

    /// <summary>
    /// 是否应在服务端落盘（UI 不负责 Save）。
    /// </summary>
    private static bool ShouldPersistEvent(IMessageEvent evt) => evt switch
    {
        SchemaSnapshotEvent => true,
        StreamCompleteEvent => true,
        ToolCallEvent
        {
            Status: ToolCallStatus.Pending or ToolCallStatus.Success
            or ToolCallStatus.Failed or ToolCallStatus.Rejected or ToolCallStatus.Cancelled
        } => true,
        LoopCompleteEvent or LoopCancelledEvent or ErrorEvent => true,
        _ => false
    };

    /// <summary>
    /// Writes <see cref="SessionData.ChannelRoute"/> only when the session route is empty (first-write-wins).
    /// </summary>
    /// <returns>True when the route was written.</returns>
    internal static bool TryBackfillChannelRoute(SessionData session, SessionChannelRoute? inbound)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (!IsEmptyChannelRoute(session.ChannelRoute))
            return false;

        if (inbound is null || string.IsNullOrWhiteSpace(inbound.ChannelId))
            return false;

        session.ChannelRoute = new SessionChannelRoute
        {
            ChannelId = inbound.ChannelId.Trim(),
            Kind = inbound.Kind.Trim(),
            ExternalId = inbound.ExternalId.Trim(),
            Title = string.IsNullOrWhiteSpace(inbound.Title) ? null : inbound.Title.Trim()
        };
        return true;
    }

    private static bool IsEmptyChannelRoute(SessionChannelRoute? route) =>
        route is null || string.IsNullOrWhiteSpace(route.ChannelId);

    /// <summary>
    /// Persists inbound model/mode from <see cref="ChatOptions"/> onto the session before execution.
    /// </summary>
    internal static bool ApplyInboundModelAndMode(
        SessionData session,
        string? modelId,
        string? modeId,
        IModelManager modelManager)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(modelManager);

        var changed = false;
        if (!string.IsNullOrWhiteSpace(modelId))
            changed |= modelManager.ApplyModelToSession(session, modelId);

        changed |= TryBackfillSessionAcpMode(session, modeId);

        if (changed)
            session.UpdatedAt = DateTime.Now;

        return changed;
    }

    /// <summary>
    /// Updates session ACP mode when provided in inbound options.
    /// </summary>
    internal static bool TryBackfillSessionAcpMode(SessionData session, string? modeId)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (string.IsNullOrWhiteSpace(modeId))
            return false;

        var trimmed = modeId.Trim();
        if (string.Equals(session.SelectedAcpMode ?? string.Empty, trimmed, StringComparison.Ordinal))
            return false;

        session.SelectedAcpMode = trimmed;
        return true;
    }

    /// <summary>
    /// 从输入提取用于标题生成的文本（优先 Text，否则拼接文本 Parts）。
    /// </summary>
    private static string? ResolveTitleSourceText(ChatInput? input)
    {
        if (input == null)
            return null;

        if (!string.IsNullOrWhiteSpace(input.Text))
            return input.Text;

        return null;
    }

    /// <summary>
    /// Fire-and-forget 标题确保；独立于主执行取消。
    /// </summary>
    private async Task EnsureTitleFireAndForgetAsync(
        ISessionTitleService ensuring,
        string sessionId,
        string userText,
        string? fallbackModel)
    {
        try
        {
            var title = await ensuring.TryEnsureAsync(
                sessionId,
                userText,
                fallbackModel,
                CancellationToken.None);

            if (!string.IsNullOrEmpty(title))
            {
                _eventPublisher.Publish(sessionId, new SessionTitleChangedEvent
                {
                    SessionId = sessionId,
                    Title = title
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Title ensure failed: SessionId={SessionId}", sessionId);
        }
    }

    /// <summary>
    /// 判定是否为命令输入（/ 开头、至少一个有效字符、非 // 转义）。
    /// </summary>
    private static bool IsCommandInput([NotNullWhen(true)] string? text)
        => text != null
           && text.Length > 1
           && text[0] == '/'
           && !text.StartsWith("//", StringComparison.Ordinal);

    /// <summary>
    /// 移除命令消息（按输入文本匹配最后一条 user 消息，命令文本不得残留在会话中）。
    /// </summary>
    private static void RemoveCommandMessage(SessionData session, string? inputText)
    {
        session.RemoveLastMessage(m =>
            string.Equals(m.Content, inputText, StringComparison.Ordinal));
    }

    /// <summary>
    /// Builds history from session.
    /// </summary>
    internal static List<ChatMessage> BuildHistoryFromSession(SessionData session)
    {
        var history = new List<ChatMessage>();

        // 统一消息来源：仅活跃消息（已压缩标记的旧消息保留展示但不传递给 LLM）
        // 跳过旧版空 system 仅承载 schema_snapshot 的载体（不进 LLM 历史）
        foreach (var msg in session.GetActiveMessages())
        {
            if (IsSchemaSnapshotCarrierMessage(msg))
                continue;

            if (IsRuntimeNotificationMessage(msg))
                continue;

            if (IsNonHistoryInboxMessage(msg))
                continue;

            var chatMessage = new ChatMessage
            {
                Role = msg.Role,
                Content = msg.Content,
                ReasoningContent = msg.ReasoningContent,
                ReasoningSignature = msg.ReasoningSignature,
                ToolCallId = msg.ToolCallId
            };

            if (msg.Parts != null && msg.Parts.Count > 0)
            {
                chatMessage.Parts = msg.Parts.Select(p => new ChatContentPart
                {
                    Type = p.Type,
                    Text = p.Text,
                    Url = p.Url,
                    DataBase64 = p.DataBase64,
                    MimeType = p.MimeType,
                    FileName = p.FileName
                }).ToList();
            }

            if (msg.ToolCalls != null && msg.ToolCalls.Count > 0)
            {
                chatMessage.ToolCalls = msg.ToolCalls.Select(tc => new ToolCall
                {
                    Id = tc.Id,
                    Type = tc.Type,
                    Function = new FunctionCall
                    {
                        Name = tc.Name,
                        Arguments = tc.Arguments
                    }
                }).ToList();
            }

            history.Add(chatMessage);

            // 工具结果在会话中内嵌于 assistant 消息的 ToolCalls[].Result，
            // 此处展开为独立的 tool 消息，满足 OpenAI 对 assistant(tool_calls)
            // 后必须紧跟对应 tool 消息的要求，否则重建历史会触发 400。
            if (msg.ToolCalls is { Count: > 0 })
            {
                foreach (var tc in msg.ToolCalls)
                {
                    history.Add(new ChatMessage
                    {
                        Role = ChatRole.Tool,
                        ToolCallId = tc.Id,
                        Content = ToolResultFormatting.ToModelContent(
                            string.Equals(tc.Status, "success", StringComparison.OrdinalIgnoreCase),
                            tc.Result,
                            tc.Error,
                            tc.Name,
                            tc.Status,
                            tc.Title)
                    });
                }
            }
        }

        return history;
    }

    /// <summary>
    /// 旧版空 system 仅承载 schema_snapshot，不进入 LLM 历史。
    /// </summary>
    private static bool IsSchemaSnapshotCarrierMessage(SessionMessage msg)
    {
        if (!string.Equals(msg.Role, MessageRole.System, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrEmpty(msg.Content))
            return false;
        if (msg.Parts is { Count: > 0 })
            return false;
        return msg.Metadata?.ContainsKey(ChatEventTracker.SchemaSnapshotMetadataKey) == true;
    }

    /// <summary>
    /// 运行时通知（错误/取消）不进 LLM 历史。
    /// 新数据已不再产生；此处兼容存量消息（前缀）与显式 transient 标记。
    /// </summary>
    private static bool IsRuntimeNotificationMessage(SessionMessage msg)
    {
        if (!string.Equals(msg.Role, MessageRole.System, StringComparison.OrdinalIgnoreCase))
            return false;

        if (msg.Metadata?.TryGetValue("transient", out var flag) == true)
        {
            if (flag is bool b && b)
                return true;
            // 落盘再加载后 Metadata 值为 JsonElement，需兼容
            if (flag is System.Text.Json.JsonElement je
                && je.ValueKind == System.Text.Json.JsonValueKind.True)
                return true;
        }

        if (string.IsNullOrEmpty(msg.Content))
            return false;

        return msg.Content.StartsWith("错误: ", StringComparison.Ordinal)
            || msg.Content.StartsWith("对话已取消: ", StringComparison.Ordinal)
            || msg.Content.StartsWith("压缩失败", StringComparison.Ordinal)
            || msg.Content.StartsWith("⚠️ 执行已取消", StringComparison.Ordinal)
            || msg.Content.StartsWith("❌ 执行出错", StringComparison.Ordinal);
    }

    /// <summary>
    /// 入站消息中 <c>inbox.presentation=none</c>（可持久化展示但不进 LLM 历史，如 Gateway 横幅）。
    /// 落盘再加载后 Metadata 值可能为 <see cref="JsonElement"/>，故用 <c>ToString()</c> 兼容。
    /// </summary>
    private static bool IsNonHistoryInboxMessage(SessionMessage msg)
    {
        if (msg.Metadata?.TryGetValue(InboxMetadataKeys.Presentation, out var raw) != true || raw is null)
            return false;

        var presentation = raw switch
        {
            string s => s,
            JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
            _ => raw.ToString()
        };

        return string.Equals(presentation, "none", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds agent context from execution context.
    /// </summary>
    private static AgentContext BuildAgentContext(ChatExecutionContext context, CancellationToken cancellationToken)
    {
        var agentContext = new AgentContext
        {
            SessionId = context.SessionId,
            WorkingDirectory = context.WorkingDirectory ?? context.WorkspaceRoot ?? "",
            WorkspaceRoot = context.WorkspaceRoot ?? "",
            PermissionAuthorizer = context.PermissionAuthorizer,
            CancellationToken = cancellationToken,
            ToolSchemas = context.ToolSchemas ?? Array.Empty<FunctionToolSchema>(),
            CatalogText = context.CatalogText
        };

        // 传递请求级模型选择到 Metadata（适用于 Native Agent 和 ACP Passthrough）
        // 优先级：用户选择 > Agent 配置 > 全局默认
        if (!string.IsNullOrEmpty(context.RequestModelId))
            agentContext.Metadata[AgentContextKeys.RequestModelId] = context.RequestModelId;

        if (context.RequestThinkingEffort is not null)
            agentContext.Metadata[AgentContextKeys.RequestThinkingEffort] = context.RequestThinkingEffort;

        if (!string.IsNullOrEmpty(context.AcpModeId))
            agentContext.Metadata[AgentContextKeys.AcpModeId] = context.AcpModeId;

        return agentContext;
    }

    /// <summary>
    /// Processes a command during execution.
    /// </summary>
    private async IAsyncEnumerable<IMessageEvent?> ProcessCommandAsync(
        string sessionId,
        string input,
        SessionData session,
        ChatExecutionContext context,
        ICommandRegistry commandRegistry,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var cmdName = input.Split(' ').FirstOrDefault()?.TrimStart('/') ?? "";
        var currentRuntime = context.Agent.Runtime;

        // 按 Runtime 查找命令
        var command = commandRegistry.GetCommand(cmdName, currentRuntime);
        if (command == null)
        {
            // 没有匹配的命令，透传给 Agent
            yield return null;
            yield break;
        }

        // 直接执行命令（薄上下文：仅信息传递，命令自包含会话操作，宿主不做写同步）
        var cmdContext = new CommandContext
        {
            CommandName = cmdName,
            Input = input,
            Arguments = input.Contains(' ') ? input.Substring(input.IndexOf(' ') + 1) : "",
            SessionId = sessionId,
            WorkspaceRoot = context.WorkspaceRoot,
            CancellationToken = cancellationToken
        };

        var result = await command.ExecuteAsync(cmdContext, cancellationToken);

        // 命令产生操作结果声明是否移除命令消息（默认移除），由宿主统一处理；
        // 命令可返回 false 保留（如 Skill 展开后把命令消息替换为技能内容）。
        // 未知命令不在此路径（command==null 时文本保留并作为普通消息透传）。
        if (result.RemoveCommandMessage)
        {
            RemoveCommandMessage(session, input);
        }

        // 根据 CommandResult 决定是否继续
        // 命令结果事件总是发布：短路命令据此终止本轮；继续类命令（如 /skill 展开、
        // Markdown 模板）借此携带 NeedsRefresh，让 UI 刷新被命令改写的消息内容
        // （否则内容已改并落盘但界面不刷新，需手动刷新页面才可见）。
        yield return new CommandResultEvent
        {
            SessionId = sessionId,
            CommandName = cmdName,
            Success = result.Success,
            Message = result.Success ? result.Message : result.ErrorMessage,
            NavigationTarget = result.GetNavigationTarget(),
            NeedsRefresh = result.NeedsRefresh,
            // 短路标志：宿主据此跳过 Agent 执行，不再把命令文本发给大模型
            ShouldContinue = result.ShouldContinue && !result.ShouldExit
        };

        if (!result.ShouldContinue || result.ShouldExit)
            yield break;

        // 继续执行 Agent
        yield return null;
    }

    /// <summary>
    /// 标记会话为 Error 状态（执行失败时调用）。
    /// 仅标记 SubAgent 子会话：task_status 回落判定依赖 SessionStatus，而 Root/Fork 会话无此消费方。
    /// 子会话执行失败后，ExecutionRecord 终态很快从队列移除，仅靠执行记录无法长期判定失败。
    /// </summary>
    private void MarkSessionError(ISessionManager sessionManager, string sessionId, string? errorMessage)
    {
        try
        {
            var failedSession = sessionManager.Get(sessionId);
            if (failedSession != null && failedSession.Kind == SessionKind.SubAgent)
            {
                failedSession.Status = SessionStatus.Error;
                failedSession.UpdatedAt = DateTime.Now;
                if (!string.IsNullOrEmpty(errorMessage))
                    failedSession.Metadata[SessionMetadataKeys.LastError] = errorMessage;
            }
        }
        catch (Exception statusEx)
        {
            _logger.LogWarning(statusEx, "标记会话 Error 状态失败: {SessionId}", sessionId);
        }
    }

    /// <summary>
    /// 子会话失败标记 Error 后，成功续跑（传递 task_id 继续任务）时清除 Error 标记。
    /// 否则子会话一旦失败，即使后续成功续跑，task_status 仍会误判为 error。
    /// </summary>
    private void ClearSessionError(ISessionManager sessionManager, string sessionId)
    {
        try
        {
            var session = sessionManager.Get(sessionId);
            if (session != null &&
                session.Kind == SessionKind.SubAgent &&
                session.Status == SessionStatus.Error)
            {
                session.Status = SessionStatus.Active;
                session.Metadata.Remove(SessionMetadataKeys.LastError);
                session.UpdatedAt = DateTime.Now;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "清除会话 Error 状态失败: {SessionId}", sessionId);
        }
    }

    /// <summary>
    /// Appends execution history to session metadata.
    /// </summary>
    private async Task AppendExecutionHistoryAsync(ISessionManager sessionManager, ExecutionRecord record)
    {
        var session = sessionManager.Get(record.SessionId);
        if (session == null) return;

        var historyJson = session.Metadata.GetValueOrDefault("execution_history", "[]");
        var history = JsonSerializer.Deserialize<List<ExecutionHistoryEntry>>(historyJson) ?? new();

        history.Add(new ExecutionHistoryEntry
        {
            ExecutionId = record.ExecutionId,
            Status = record.Status,
            StartedAt = record.StartedAt,
            CompletedAt = record.CompletedAt,
            ErrorMessage = record.ErrorMessage
        });

        // Limit history size
        if (history.Count > _options.ExecutionHistoryLimit)
        {
            history = history.TakeLast(_options.ExecutionHistoryLimit).ToList();
        }

        session.Metadata["execution_history"] = JsonSerializer.Serialize(history);
    }
}

/// <summary>
/// Entry for execution history.
/// </summary>
public class ExecutionHistoryEntry
{
    public string ExecutionId { get; set; } = "";
    public ExecutionStatus Status { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
}
