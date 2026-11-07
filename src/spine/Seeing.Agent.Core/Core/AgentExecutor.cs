using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Prompts;
using Seeing.Agent.Abstractions.Reminders;
using Seeing.Agent.Abstractions.Todo;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Detection;
using Seeing.Agent.Core.Execution;
using Seeing.Agent.Core.Llm;
using Seeing.Agent.Core.Permission;
using Seeing.Agent.Core.Prompts;
using Seeing.Agent.Core.Reminders;
using Seeing.Agent.Core.Todo;
using Seeing.Agent.Core.Tools;
using Seeing.Agent.Llm;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
namespace Seeing.Agent.Core;

/// <summary>
/// Agent 执行器 - 统一执行引擎
/// <para>
/// 返回事件流（IMessageEvent），由调用方决定如何渲染和存储。
/// </para>
/// </summary>
public class AgentExecutor : IAgentExecutor
{
    private readonly ILlmService _llm;
    private readonly ToolManager _tools;
    private readonly IPermissionService _permissions;
    private readonly IHookManager _hooks;
    private readonly IAgentRegistry _registry;
    private readonly PromptBuilder _promptBuilder;
    private readonly IModelManager _modelManager;
    private readonly ILogger<AgentExecutor> _logger;
    private readonly Seeing.Session.Core.ISessionManager? _sessionManager;
    private readonly ITodoStore? _todoStore;
    private readonly ILlmTurnRetryPolicy? _turnRetryPolicy;

    /// <summary>单个工具调用的执行体（可见集校验 → 权限 → 执行 → 终态事件）。</summary>
    private readonly ToolCallInvoker _toolInvoker;

    /// <summary>
    /// 构造 Agent 执行器，注入 LLM、工具、权限、钩子、注册表等核心依赖（后四项为可选）。
    /// </summary>
    public AgentExecutor(
        ILlmService llm,
        ToolManager tools,
        IPermissionService permissions,
        IHookManager hooks,
        IAgentRegistry registry,
        PromptBuilder promptBuilder,
        IModelManager modelManager,
        ILogger<AgentExecutor> logger,
        Seeing.Session.Core.ISessionManager? sessionManager = null,
        ITodoStore? todoStore = null,
        ILlmTurnRetryPolicy? turnRetryPolicy = null)
    {
        _llm = llm;
        _tools = tools;
        _permissions = permissions;
        _hooks = hooks;
        _registry = registry;
        _promptBuilder = promptBuilder;
        _modelManager = modelManager;
        _logger = logger;
        _sessionManager = sessionManager;
        _todoStore = todoStore;
        _turnRetryPolicy = turnRetryPolicy;
        _toolInvoker = new ToolCallInvoker(tools, permissions, logger, sessionManager);
    }

    /// <summary>
    /// 按 OrdinalIgnoreCase 在注册表中查找（<see cref="ToolManager"/> 的查找是序数敏感的）。
    /// <para>
    /// <b>本方法是注册表枚举的唯一调用点</b>（见 ToolViewSingleSourceTests）：
    /// 只用于把「已注册但被本轮结算过滤」与「根本不存在」区分成两种拒绝文案，
    /// 判定本轮可用性的始终是结算快照（<c>ResolveCanonicalId</c>），不是这次枚举。
    /// </para>
    /// </summary>
    private ITool? GetRegisteredToolIgnoreCase(string name) =>
        _tools.GetRegisteredTools()
            .FirstOrDefault(t => string.Equals(t.Id, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 执行 Agent - 核心 LLM 循环
    /// </summary>
    /// <param name="agent">Agent 定义</param>
    /// <param name="messages">输入消息流</param>
    /// <param name="context">执行上下文</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>事件流（流式增量、完整消息、工具调用、错误等）</returns>
    public async IAsyncEnumerable<IMessageEvent> ExecuteAsync(
        Seeing.Agent.Abstractions.Agents.AgentDefinition agent,
        IReadOnlyList<ChatMessage> messages,
        AgentContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // 检查禁用状态（保持原行为：直接产出错误，无需走事件流水线）
        if (agent.Disabled)
        {
            yield return new ErrorEvent
            {
                SessionId = context.SessionId,
                Message = $"Agent '{agent.Name}' is disabled"
            };
            yield break;
        }

        // 合并取消令牌
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            context.CancellationToken, cancellationToken);
        var effectiveToken = linkedCts.Token;

        // AgentExecutor 为 Singleton：所有执行期可变状态挂 per-execution 实例，避免并发 Loop 互相踩踏
        var state = new LoopExecutionState();

        // 事件流水线：生产者保证「LoopStart → 恰一个终态事件」；取消/异常在生产者侧转换为终态事件。
        // 与 ACP 执行器（EventYieldingSink + runTask）同一模式，消费侧只需正常读取。
        var outbound = Channel.CreateUnbounded<IMessageEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        var producer = RunProducerAsync(agent, messages, context, effectiveToken, outbound.Writer, state);

        try
        {
            await foreach (var evt in outbound.Reader.ReadAllAsync(CancellationToken.None))
                yield return evt;
        }
        finally
        {
            // 生产者内部已兜底终态事件；此处仅防御性等待，避免异常从 finally 逃逸
            try { await producer; }
            catch { _logger.LogWarning("[AgentExecutor] 生产者异常已由内部兜底，忽略"); }
        }
    }

    /// <summary>
    /// 事件生产者：负责整条 Loop 事件流的产出与「终态不变量」。
    /// 取消（OCE）→ LoopCancelledEvent；未知异常 → ErrorEvent + LoopCompleteEvent(失败)。
    /// </summary>
    private async Task RunProducerAsync(
        Seeing.Agent.Abstractions.Agents.AgentDefinition agent,
        IReadOnlyList<ChatMessage> messages,
        AgentContext context,
        CancellationToken effectiveToken,
        ChannelWriter<IMessageEvent> writer,
        LoopExecutionState state)
    {
        // ========== 生成 LoopId（一次完整对话循环的唯一标识）==========
        var loopId = Guid.NewGuid().ToString("N");
        var loopStartTime = DateTime.Now;
        var totalSteps = 0;
        TokenUsage? totalUsage = null;
        string? errorMessage = null;

        var maxSteps = agent.MaxSteps ?? 9999;
        var history = messages.ToList();

        // 执行级权限授权器（能力门 / 资源门共用）；缺失时能力门 NeedsConfirmation 按拒绝处理
        var permissionAuthorizer = context.PermissionAuthorizer;

        // 执行级工具可见性作用域：种子来自本轮结算视图，生命周期与本 Loop 相同。
        // 只放宽执行层放行判定（tool_search 命中即可调用），不改变本轮 tools[]。
        // 无结算 = 无执行级放行能力，对工具必须表现为"无作用域"（否则检索工具会误判支持）。
        var toolScope = context.ToolSettlement is null
            ? null
            : new ExecutionToolScope(context.ToolSettlement);

        // SubAgent：合并 Session PermissionSnapshot 作为本 Loop 权限真相源
        context.PermissionContext = PermissionIntegrity.FromAgentContext(
            context, ResolvePolicy(agent, context), agent.Name);

        try
        {
            // ========== 发布 LoopStart 事件 ==========
            await writer.WriteAsync(new LoopStartEvent
            {
                SessionId = context.SessionId,
                LoopId = loopId,
                UserInput = history.LastOrDefault()?.Content
            }, effectiveToken);

            for (var step = 0; step < maxSteps; step++)
            {
                effectiveToken.ThrowIfCancellationRequested();
                totalSteps = step + 1;

                // 暂停项只在本轮第一次请求前追加到消息末尾，不改系统提示（保住前缀缓存）。
                if (step == 0)
                    await AppendPausedTodoReminderAsync(history, context).ConfigureAwait(false);

                ChatMessage? assistantMessage = null;
                Exception? llmException = null;
                TokenUsage? lastUsage = null;
                var turnElapsed = TimeSpan.Zero;

                for (var attempt = 1; ; attempt++)
                {
                    // 每次 attempt 重新构建请求：传输层适配（温度/上限/思考强度）会改写请求对象，
                    // 跨重试复用会让下一次调用读到上一次已被修改的值。
                    var request = await BuildRequestAsync(agent, history, context, effectiveToken).ConfigureAwait(false);

                    assistantMessage = null;            // 每次 attempt 重置，避免残留上一尝试结果
                    var usageBeforeTurn = totalUsage;   // 重试失败时回滚本轮 usage 累加
                    var streamingContent = new StringBuilder();
                    var streamingReasoning = new StringBuilder();
                    string? streamingReasoningSignature = null;
                    List<ToolCall>? accumulatedToolCalls = null;
                    lastUsage = null;
                    llmException = null;

                    await writer.WriteAsync(new StreamStartEvent
                    {
                        SessionId = context.SessionId,
                        LoopId = loopId,
                        Step = step,
                        Attempt = attempt
                    }, effectiveToken);

                    var llmChannel = Channel.CreateUnbounded<StreamUpdate>();
                    var llmTask = Task.Run(async () =>
                    {
                        try
                        {
                            await foreach (var update in _llm.CompleteStreamAsync(
                                ResolveModelId(agent, context), request, context.SessionId, effectiveToken))
                            {
                                await llmChannel.Writer.WriteAsync(update, effectiveToken).ConfigureAwait(false);
                            }
                            llmChannel.Writer.Complete();
                        }
                        catch (OperationCanceledException ex) when (ex.CancellationToken == effectiveToken)
                        {
                            _logger.LogInformation("[AgentExecutor] LLM 请求被取消");
                            llmChannel.Writer.Complete();
                        }
                        catch (LlmRetryExhaustedException ex)
                        {
                            llmException = ex;
                            _logger.LogError(ex, "[AgentExecutor] LLM 重试耗尽: {Message}", ex.Message);
                            llmChannel.Writer.Complete();
                        }
                        catch (LlmTimeoutException ex)
                        {
                            llmException = ex;
                            _logger.LogError(ex, "[AgentExecutor] LLM 请求超时");
                            llmChannel.Writer.Complete();
                        }
                        catch (LlmStreamingException ex)
                        {
                            llmException = ex;
                            _logger.LogError(ex, "[AgentExecutor] LLM 流式错误: {Message}", ex.Message);
                            llmChannel.Writer.Complete();
                        }
                        catch (LlmException ex)
                        {
                            llmException = ex;
                            _logger.LogError(ex, "[AgentExecutor] LLM 错误: {Message}", ex.Message);
                            llmChannel.Writer.Complete();
                        }
                        catch (IOException ex)
                        {
                            llmException = new LlmConnectionException("网络连接错误", ex);
                            _logger.LogError(ex, "[AgentExecutor] 网络连接错误");
                            llmChannel.Writer.Complete();
                        }
                        catch (Exception ex)
                        {
                            llmException = new LlmException($"LLM 调用失败: {ex.Message}", ex);
                            _logger.LogError(ex, "[AgentExecutor] LLM 调用失败");
                            llmChannel.Writer.Complete();
                        }
                    }, effectiveToken);

                    await foreach (var update in llmChannel.Reader.ReadAllAsync(effectiveToken))
                    {
                        if (!string.IsNullOrEmpty(update.ReasoningDelta))
                        {
                            streamingReasoning.Append(update.ReasoningDelta);
                            await writer.WriteAsync(new StreamDeltaEvent
                            {
                                SessionId = context.SessionId,
                                LoopId = loopId,
                                ReasoningDelta = update.ReasoningDelta
                            }, effectiveToken);
                        }

                        if (!string.IsNullOrEmpty(update.ReasoningSignature))
                            streamingReasoningSignature = update.ReasoningSignature;

                        if (!string.IsNullOrEmpty(update.ContentDelta))
                        {
                            streamingContent.Append(update.ContentDelta);
                            await writer.WriteAsync(new StreamDeltaEvent
                            {
                                SessionId = context.SessionId,
                                LoopId = loopId,
                                ContentDelta = update.ContentDelta
                            }, effectiveToken);
                        }

                        if (update.ToolCallDeltas != null && update.ToolCallDeltas.Count > 0)
                        {
                            accumulatedToolCalls ??= new List<ToolCall>();
                            accumulatedToolCalls.AddRange(update.ToolCallDeltas);
                        }

                        if (update.Usage != null)
                        {
                            lastUsage = update.Usage;
                            totalUsage = totalUsage == null
                                ? new TokenUsage
                                {
                                    InputTokens = update.Usage.InputTokens,
                                    OutputTokens = update.Usage.OutputTokens
                                }
                                : totalUsage with
                                {
                                    InputTokens = totalUsage.InputTokens + update.Usage.InputTokens,
                                    OutputTokens = totalUsage.OutputTokens + update.Usage.OutputTokens
                                };
                        }

                        if (update.IsComplete)
                        {
                            assistantMessage = BuildAssistantMessage(
                                update,
                                streamingContent.ToString(),
                                streamingReasoning.ToString(),
                                streamingReasoningSignature,
                                accumulatedToolCalls);
                        }
                    }

                    await llmTask.ConfigureAwait(false);

                    if (effectiveToken.IsCancellationRequested)
                        throw new OperationCanceledException(effectiveToken);

                    if (llmException == null)
                        break;

                    // 应用层整轮重试判定（本轮工具尚未执行，回滚该轮产出安全）
                    if (_turnRetryPolicy is not { Enabled: true }
                        || !_turnRetryPolicy.CanRetry(llmException, effectiveToken))
                        break;

                    var nextDelay = _turnRetryPolicy.NextDelay(attempt);
                    if (!_turnRetryPolicy.ShouldRetry(attempt, turnElapsed, nextDelay))
                        break;

                    turnElapsed += nextDelay;
                    await writer.WriteAsync(new LlmRetryScheduledEvent
                    {
                        SessionId = context.SessionId,
                        LoopId = loopId,
                        Step = step,
                        Attempt = attempt + 1,
                        NextDelayMs = (int)nextDelay.TotalMilliseconds,
                        Reason = llmException.Message
                    }, effectiveToken);

                    // 回滚本轮 usage 累加，避免重试重复计入
                    totalUsage = usageBeforeTurn;

                    _logger.LogWarning(
                        "[AgentExecutor] 轮次重试: Step={Step}, Attempt={Attempt}, NextDelayMs={DelayMs}",
                        step, attempt + 1, nextDelay.TotalMilliseconds);

                    await Task.Delay(nextDelay, effectiveToken).ConfigureAwait(false);
                }

                // 处理 LLM 异常
                if (llmException != null)
                {
                    errorMessage = llmException.Message;

                    _hooks.TriggerFireAndForget(
                        HookRegistry.LlmOnError,
                        context.SessionId,
                        new Dictionary<string, object?>
                        {
                            ["modelId"] = ResolveModelId(agent, context),
                            ["error"] = llmException,
                            ["source"] = llmException is LlmException lle ? lle.Source : "unknown"
                        });

                    await writer.WriteAsync(new ErrorEvent
                    {
                        SessionId = context.SessionId,
                        LoopId = loopId,
                        Message = llmException.Message,
                        Exception = llmException,
                        Source = "llm"
                    }, CancellationToken.None);

                    await writer.WriteAsync(new LoopCompleteEvent
                    {
                        SessionId = context.SessionId,
                        LoopId = loopId,
                        TotalSteps = totalSteps,
                        Duration = DateTime.Now - loopStartTime,
                        Success = false,
                        Error = errorMessage
                    }, CancellationToken.None);
                    return;
                }

                if (assistantMessage == null)
                {
                    _logger.LogWarning("[AgentExecutor] LLM 返回空响应");
                    errorMessage = "LLM 返回空响应";

                    await writer.WriteAsync(new ErrorEvent
                    {
                        SessionId = context.SessionId,
                        LoopId = loopId,
                        Message = "LLM 返回空响应",
                        Source = "llm"
                    }, CancellationToken.None);

                    await writer.WriteAsync(new LoopCompleteEvent
                    {
                        SessionId = context.SessionId,
                        LoopId = loopId,
                        TotalSteps = totalSteps,
                        Duration = DateTime.Now - loopStartTime,
                        Success = false,
                        Error = errorMessage
                    }, CancellationToken.None);
                    return;
                }

                history.Add(assistantMessage);

                // ========== 发布 StreamComplete 事件 ==========
                await writer.WriteAsync(new StreamCompleteEvent
                {
                    SessionId = context.SessionId,
                    LoopId = loopId,
                    Message = assistantMessage,
                    Usage = lastUsage
                }, effectiveToken);

                // 无工具调用则检查 todo 状态。未完成只拦一次，第二次放行。
                if (assistantMessage.ToolCalls == null || assistantMessage.ToolCalls.Count == 0)
                {
                    if (await HoldForIncompleteTodosAsync(history, context, state).ConfigureAwait(false))
                        continue;

                    // 发布 LoopComplete 事件（成功）
                    await writer.WriteAsync(new LoopCompleteEvent
                    {
                        SessionId = context.SessionId,
                        LoopId = loopId,
                        TotalSteps = totalSteps,
                        Duration = DateTime.Now - loopStartTime,
                        Success = true,
                        Usage = totalUsage
                    }, CancellationToken.None);
                    return;
                }

                // ========== 执行工具调用（内部排空，保证终态送达）==========
                var (endTurn, endTurnReason) = await ExecuteToolCallsAsync(
                    assistantMessage.ToolCalls ?? new List<ToolCall>(),
                    agent,
                    context,
                    permissionAuthorizer,
                    loopId,
                    writer,
                    history,
                    effectiveToken,
                    state,
                    toolScope);

                // 循环检测终止：优先于 EndTurn，发出失败终态 + 循环检测原因
                if (state.TerminateRequested)
                {
                    errorMessage = state.TerminationReason ?? "检测到重复的工具调用循环，已终止";

                    await writer.WriteAsync(new LoopCompleteEvent
                    {
                        SessionId = context.SessionId,
                        LoopId = loopId,
                        TotalSteps = totalSteps,
                        Duration = DateTime.Now - loopStartTime,
                        Success = false,
                        Error = errorMessage,
                        Reason = "loop-detected"
                    }, CancellationToken.None);
                    return;
                }

                // 工具要求结束本轮。未完成 todo 与无工具退出共用一次拦截；再次 EndTurn 则放行。
                if (endTurn)
                {
                    if (await HoldForIncompleteTodosAsync(history, context, state).ConfigureAwait(false))
                        continue;

                    await writer.WriteAsync(new LoopCompleteEvent
                    {
                        SessionId = context.SessionId,
                        LoopId = loopId,
                        TotalSteps = totalSteps,
                        Duration = DateTime.Now - loopStartTime,
                        Success = true,
                        Reason = endTurnReason ?? "turn-directive"
                    }, CancellationToken.None);
                    return;
                }
            }

            // 达到最大步数
            errorMessage = $"达到最大步数 {maxSteps}，已停止";

            await writer.WriteAsync(new ErrorEvent
            {
                SessionId = context.SessionId,
                LoopId = loopId,
                Message = errorMessage,
                Source = "agent"
            }, CancellationToken.None);

            await writer.WriteAsync(new LoopCompleteEvent
            {
                SessionId = context.SessionId,
                LoopId = loopId,
                TotalSteps = totalSteps,
                Duration = DateTime.Now - loopStartTime,
                Success = false,
                Error = errorMessage
            }, CancellationToken.None);
        }
        catch (OperationCanceledException) when (effectiveToken.IsCancellationRequested)
        {
            _logger.LogInformation("[AgentExecutor] Loop 已取消，发布 LoopCancelledEvent");
            await writer.WriteAsync(new LoopCancelledEvent
            {
                SessionId = context.SessionId,
                LoopId = loopId,
                Reason = "user",
                CompletedSteps = totalSteps,
                PartialUsage = totalUsage
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AgentExecutor] Loop 未捕获异常，发布 Error + LoopComplete(失败)");
            await writer.WriteAsync(new ErrorEvent
            {
                SessionId = context.SessionId,
                LoopId = loopId,
                Message = ex.Message,
                Exception = ex,
                Source = "agent"
            }, CancellationToken.None);
            await writer.WriteAsync(new LoopCompleteEvent
            {
                SessionId = context.SessionId,
                LoopId = loopId,
                TotalSteps = totalSteps,
                Duration = DateTime.Now - loopStartTime,
                Success = false,
                Error = ex.Message
            }, CancellationToken.None);
        }
        finally
        {
            writer.Complete();
        }
    }

    /// <summary>
    /// 构建聊天请求
    /// </summary>
    private async Task<ChatRequest> BuildRequestAsync(
        Seeing.Agent.Abstractions.Agents.AgentDefinition agent,
        List<ChatMessage> messages,
        AgentContext context,
        CancellationToken cancellationToken)
    {
        // 工具视图唯一出口在 Hosting 结算；此处只读 context.ToolSettlement
        var settlement = context.ToolSettlement;
        var toolSchemas = settlement?.InlineSchemas
            ?? (IReadOnlyList<FunctionToolSchema>)Array.Empty<FunctionToolSchema>();
        // 使用 PromptBuilder 构建系统提示词
        // 不再以「agent 有无系统提示词」守卫整块：上下文基线即使在没有正文时也必须送达模型
        // （PromptBuilder 在正文为空时直接返回基线）。
        string? systemPrompt;
        var promptContext = new PromptContext
        {
            Agent = agent,
            SessionId = context.SessionId,
            WorkingDirectory = context.WorkingDirectory,
            WorkspaceRoot = context.WorkspaceRoot,
            ModelName = ResolveModelId(agent, context),
            Timestamp = DateTime.Now,
            Platform = Environment.OSVersion.Platform.ToString(),
            Tools = toolSchemas.Select(ts => ts.Function).ToList(),
            ContextBaseline = context.ContextBaseline,
        };

        systemPrompt = await _promptBuilder.BuildAsync(promptContext, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(systemPrompt))
            systemPrompt = null;

        // 转换 FunctionToolSchema 到 ToolDefinition
        var tools = toolSchemas.Select(s => new ToolDefinition
        {
            Type = "function",
            Function = new FunctionDefinition
            {
                Name = s.Function.Name,
                Description = s.Function.Description,
                Parameters = s.Function.Parameters
            }
        }).ToList();

        return new ChatRequest
        {
            Messages = messages,
            SystemPrompt = systemPrompt,
            Tools = tools.Count > 0 ? tools : null,
            Temperature = agent.Temperature,
            TopP = agent.TopP,
            MaxTokens = agent.MaxTokens,
            Stream = true,
            ThinkingEffort = ResolveThinkingEffort(context)
        };
    }

    private static string? ResolveThinkingEffort(AgentContext context)
    {
        if (context.Metadata.TryGetValue(AgentContextKeys.RequestThinkingEffort, out var raw) && raw is string s)
            return s;
        return null;
    }

    /// <summary>
    /// 工具终态事件排空时限（仅取消路径兜底）：防止个别不响应取消的工具无限阻塞 Loop 终态。
    /// 正常路径不设超时。测试可通过 internal setter 缩短以加速回归。
    /// </summary>
    internal static TimeSpan ToolDrainTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 执行工具调用 - 直接写入事件流。
    /// 关键：消费端用 CancellationToken.None 排空，取消后工具终态事件（Cancelled/Success/Failed）完整送达；
    /// 取消信号与排空完成竞争，避免「取消只检查一次」导致工具开始后才取消的会话永久挂起；
    /// 取消后仍给予 <see cref="ToolDrainTimeout"/> 让配合取消的工具送出终态，超时则放弃排空并返回。
    /// </summary>
    /// <returns>本步是否存在 EndTurn 工具结果，及其透传的原因（无则 null）。</returns>
    private async Task<(bool EndTurn, string? Reason)> ExecuteToolCallsAsync(
        List<ToolCall> toolCalls,
        Seeing.Agent.Abstractions.Agents.AgentDefinition agent,
        AgentContext context,
        IPermissionAuthorizer? permissionAuthorizer,
        string loopId,
        ChannelWriter<IMessageEvent> writer,
        List<ChatMessage> history,
        CancellationToken cancellationToken,
        LoopExecutionState state,
        IExecutionToolScope toolScope)
    {
        // ========== Doom Loop 检测（单一入口：Check 同时记录并返回判定）==========
        // 以 (toolId, SHA256(参数 JSON)) 为键，连续 3 次警告、≥5 次终止本 Loop。
        foreach (var tc in toolCalls)
        {
            var detectedName = tc.Function?.Name ?? "";
            var argumentsHash = LoopDetector.ComputeArgumentsHash(tc.Function?.Arguments ?? string.Empty);
            var detection = state.LoopDetector.Check(detectedName, argumentsHash);

            if (detection.RecommendedAction == LoopAction.Warn)
            {
                await writer.WriteAsync(new ErrorEvent
                {
                    SessionId = context.SessionId,
                    LoopId = loopId,
                    Message = $"检测到重复的工具调用：{detectedName} 已连续 {detection.ConsecutiveCount} 次，请调整策略",
                    Source = "loop"
                }, cancellationToken);
            }
            else if (detection.RecommendedAction == LoopAction.Terminate)
            {
                state.TerminateRequested = true;
                state.TerminationReason =
                    $"检测到重复的工具调用循环：{detectedName} 连续 {detection.ConsecutiveCount} 次相同调用，已终止";
                _logger.LogWarning(
                    "[AgentExecutor] Loop 检测到重复工具调用并终止: {ToolName} x{Count}",
                    detectedName, detection.ConsecutiveCount);
                return (true, state.TerminationReason);
            }
        }

        // 先全部 Pending，再并行执行；执行中通过 Channel 交错 Running / Emit / Complete
        foreach (var tc in toolCalls)
        {
            await writer.WriteAsync(new ToolCallEvent
            {
                SessionId = context.SessionId,
                LoopId = loopId,
                Type = MessageEventType.ToolCallPending,
                ToolCallId = tc.Id,
                ToolName = tc.Function?.Name ?? "",
                Arguments = ParseArguments(tc.Function?.Arguments),
                Status = ToolCallStatus.Pending
            }, cancellationToken);
        }

        var channel = Channel.CreateUnbounded<IMessageEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        var runTasks = toolCalls.Select(tc => RunToolCallWithEventsAsync(
            tc, agent, context, permissionAuthorizer, loopId, channel.Writer, toolScope, cancellationToken)).ToList();

        var allDone = Task.WhenAll(runTasks).ContinueWith(
            t =>
            {
                channel.Writer.TryComplete(t.Exception?.InnerException ?? t.Exception);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        // 排空转发：把每个工具终态事件写入 outbound，并同步追加 tool 历史。
        var forward = ForwardToolEventsAsync(channel.Reader, writer, history);

        // 取消必须可观测：不依赖工具是否配合取消。
        // 取消信号与「排空完成」竞争；取消后仍给予 ToolDrainTimeout 让响应取消的工具送出终态，
        // 超时则放弃本次排空并返回，避免整个会话被不响应取消的工具挂死（只能重启进程）。
        var drain = Task.WhenAll(forward, allDone);
        if (cancellationToken.CanBeCanceled)
        {
            var cancelSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(
                static state => ((TaskCompletionSource)state!).TrySetResult(),
                cancelSignal);

            if (!ReferenceEquals(await Task.WhenAny(drain, cancelSignal.Task).ConfigureAwait(false), drain))
            {
                // 已取消：有限时间内等待工具终态事件送达
                var drained = await Task.WhenAny(drain, Task.Delay(ToolDrainTimeout, CancellationToken.None))
                    .ConfigureAwait(false);

                if (!ReferenceEquals(drained, drain))
                {
                    _logger.LogWarning(
                        "[AgentExecutor] 工具终态事件排空超时({TimeoutSeconds}s)，跳过剩余工具事件",
                        ToolDrainTimeout.TotalSeconds);
                    channel.Writer.TryComplete();
                    try { await forward.ConfigureAwait(false); } catch { /* 个别事件丢弃为已知边界 */ }
                    return (false, null); // 不等待不响应取消的工具（任务泄漏为已知边界，与修复前一致）
                }
            }
        }

        // 排空成功（或未启用的取消令牌）：forward 与 allDone 均已结束
        await allDone.ConfigureAwait(false);
        return await forward.ConfigureAwait(false);
    }

    /// <summary>
    /// 转发工具事件并同步追加 tool 历史（所有终态都必须有对应的 tool 消息，否则下一轮 LLM 报 400）。
    /// 转发本身不因取消中断：消费端统一用 CancellationToken.None 排空。
    /// </summary>
    /// <returns>本步是否存在 EndTurn 工具结果，及其透传的原因（无则 null）。</returns>
    private static async Task<(bool EndTurn, string? Reason)> ForwardToolEventsAsync(
        ChannelReader<IMessageEvent> reader,
        ChannelWriter<IMessageEvent> outbound,
        List<ChatMessage> history)
    {
        var endTurn = false;
        string? reason = null;
        await foreach (var evt in reader.ReadAllAsync(CancellationToken.None))
        {
            await outbound.WriteAsync(evt, CancellationToken.None);
            if (evt is ToolCallEvent tcEvent &&
                tcEvent.Status is ToolCallStatus.Success or ToolCallStatus.Failed
                    or ToolCallStatus.Rejected or ToolCallStatus.Cancelled)
            {
                if (tcEvent.TurnDirective == ToolTurnDirective.EndTurn)
                {
                    endTurn = true;
                    reason = tcEvent.TurnDirectiveReason ?? reason;
                }

                history.Add(new ChatMessage
                {
                    Role = ChatRole.Tool,
                    ToolCallId = tcEvent.ToolCallId,
                    Content = tcEvent.ModelContent
                });
            }
        }
        return (endTurn, reason);
    }

    private async Task RunToolCallWithEventsAsync(
        ToolCall tc,
        Seeing.Agent.Abstractions.Agents.AgentDefinition agent,
        AgentContext context,
        IPermissionAuthorizer? permissionAuthorizer,
        string loopId,
        ChannelWriter<IMessageEvent> writer,
        IExecutionToolScope toolScope,
        CancellationToken cancellationToken)
    {
        var name = tc.Function?.Name ?? "";

        // ========== 可见集校验（在派发前完成） ==========
        // 只拦「不在本会话结算内」的工具（被禁用 / Agent 过滤 / 模块未挂载 / 名字不存在）。
        // 结算内即放行：deferred（未下发 schema）也能直接调用——目录已给出参数签名，
        // 参数错误由失败时的定义回灌兜住。加载（TryLoad）只是审计，不参与能否调用。
        // <b>此处是可用性的唯一判定点</b>（ToolViewSingleSourceTests 锁定）；ToolCallInvoker 只做权限与执行。
        if (context.ToolSettlement is { } settlement)
        {
            // 结算用 OrdinalIgnoreCase、ToolManager 用序数敏感：先归一化为结算里的 canonical id，
            // 否则模型写 Read 会穿过 gate 后拿到裸「工具不存在: Read」。
            var canonicalName = settlement.ResolveCanonicalId(name);
            if (canonicalName is null)
            {
                var isEnabled = _tools.IsToolEnabled(name);
                var isRegistered = GetRegisteredToolIgnoreCase(name) is not null;
                var reason = ToolVisibilityGate.ResolveVisibilityRejection(name, context, isEnabled, isRegistered);
                _logger.LogWarning(
                    "[AgentExecutor] 拒绝了结算外工具的调用: {ToolName} (enabled={Enabled}, registered={Registered})",
                    name, isEnabled, isRegistered);

                await writer.WriteAsync(new ToolCallEvent
                {
                    SessionId = context.SessionId,
                    LoopId = loopId,
                    Type = MessageEventType.ToolCallComplete,
                    ToolCallId = tc.Id,
                    ToolName = name,
                    Arguments = ParseArguments(tc.Function?.Arguments),
                    Status = ToolCallStatus.Rejected,
                    Error = reason,
                    Metadata = ToolVisibilityGate.BuildVisibilityMetadata(isEnabled, isRegistered)
                }, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            if (!string.Equals(canonicalName, name, StringComparison.Ordinal))
            {
                _logger.LogDebug(
                    "[AgentExecutor] 工具名大小写归一化: {Requested} → {Canonical}", name, canonicalName);
                name = canonicalName;
                // ToolManager 按 toolCall.Name 查注册表（序数敏感）：必须把归一化后的名字传下去。
                tc = new ToolCall
                {
                    Id = tc.Id,
                    Type = tc.Type,
                    Function = new FunctionCall
                    {
                        Name = canonicalName,
                        Arguments = tc.Function?.Arguments
                    }
                };
            }
        }

        await writer.WriteAsync(new ToolCallEvent
        {
            SessionId = context.SessionId,
            LoopId = loopId,
            Type = MessageEventType.ToolCallRunning,
            ToolCallId = tc.Id,
            ToolName = name,
            Arguments = ParseArguments(tc.Function?.Arguments),
            Status = ToolCallStatus.Running
        }, cancellationToken).ConfigureAwait(false);

        ValueTask EmitAsync(IMessageEvent evt) =>
            new(writer.WriteAsync(evt, cancellationToken).AsTask());

        ToolCallEvent complete;
        try
        {
            complete = await _toolInvoker.InvokeAsync(
                tc, agent, context, permissionAuthorizer, loopId, toolScope, EmitAsync, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            complete = BuildTerminalEvent(tc, name, context, loopId, ToolCallStatus.Cancelled, "已取消");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AgentExecutor] 工具执行异常未捕获: {ToolName}", name);
            complete = BuildTerminalEvent(tc, name, context, loopId, ToolCallStatus.Failed, ex.Message);
        }

        // 使用 CancellationToken.None 确保终态事件即使在被取消时也能写出。
        // 通道可能已被「取消后放弃排空」关闭：此任务此时已被上层放弃，写失败只记录，不得向外抛。
        try
        {
            await writer.WriteAsync(complete, CancellationToken.None).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            _logger.LogDebug(
                "[AgentExecutor] 工具终态事件通道已关闭，跳过写入: {ToolName}", name);
        }
    }

    private static ToolCallEvent BuildTerminalEvent(
        ToolCall tc,
        string name,
        AgentContext context,
        string loopId,
        ToolCallStatus status,
        string error,
        IReadOnlyDictionary<string, object>? metadata = null)
    {
        return new ToolCallEvent
        {
            SessionId = context.SessionId,
            LoopId = loopId,
            Type = MessageEventType.ToolCallComplete,
            ToolCallId = tc.Id,
            ToolName = name,
            Arguments = ParseArguments(tc.Function?.Arguments),
            Status = status,
            Error = error,
            Metadata = metadata
        };
    }

    /// <summary>
    /// 解析权限策略：SubAgent Session 合并 PermissionSnapshot。
    /// </summary>
    private AgentPermissionPolicy ResolvePolicy(Seeing.Agent.Abstractions.Agents.AgentDefinition agent, AgentContext context)
    {
        var policy = agent.BuildPermissionPolicy();
        var session = _sessionManager?.Get(context.SessionId);
        if (session?.Kind == Seeing.Session.Core.SessionKind.SubAgent &&
            session.PermissionSnapshot.Count > 0)
        {
            return SessionPermissionMapper.ApplySnapshot(policy, session.PermissionSnapshot);
        }

        return policy;
    }

    /// <summary>
    /// 解析模型 ID
    /// </summary>
    private string ResolveModelId(Seeing.Agent.Abstractions.Agents.AgentDefinition agent, AgentContext? context = null)
    {
        string? requestModelRef = null;
        if (context?.Metadata != null &&
            context.Metadata.TryGetValue(AgentContextKeys.RequestModelId, out var requestModelObj) &&
            requestModelObj is string requestModel &&
            !string.IsNullOrEmpty(requestModel))
        {
            requestModelRef = requestModel;
            _logger.LogDebug("[AgentExecutor] 使用请求/会话模型: {Model}", requestModelRef);
        }

        var resolved = _modelManager.ResolveNativeModel(requestModelRef, null, agent.Name);
        if (!string.IsNullOrEmpty(resolved))
            return resolved;

        throw new InvalidOperationException(
            $"Agent '{agent.Name}' 未配置模型，且未设置 chat 能力的全局默认模型（SeeingAgent:DefaultModels.chat）。" +
            $"请在 Agent 定义中设置 Model，或在配置中设置 DefaultModels[\"chat\"]。");
    }

    /// <summary>
    /// 构建 Assistant 消息
    /// </summary>
    private static ChatMessage BuildAssistantMessage(
        StreamUpdate update,
        string fullContent,
        string fullReasoning,
        string? reasoningSignature,
        List<ToolCall>? toolCalls)
    {
        return new ChatMessage
        {
            Role = ChatRole.Assistant,
            Content = fullContent,
            ReasoningContent = string.IsNullOrEmpty(fullReasoning) ? null : fullReasoning,
            ReasoningSignature = string.IsNullOrEmpty(reasoningSignature) ? null : reasoningSignature,
            ToolCalls = toolCalls
        };
    }

    /// <summary>
    /// 解析 JSON 参数
    /// </summary>
    private static object? ParseArguments(string? arguments)
    {
        if (string.IsNullOrEmpty(arguments))
            return null;

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object>>(arguments);
        }
        catch
        {
            return arguments;
        }
    }

    /// <summary>
    /// 本轮工具 schema 是否包含 todowrite。没有该工具时不做 todo 编排。
    /// </summary>
    private static bool HasTodoWriteTool(AgentContext context) =>
        context.ToolSettlement?.InlineSchemas.Any(s =>
            string.Equals(s.Function?.Name, "todowrite", StringComparison.OrdinalIgnoreCase)) == true;

    /// <summary>
    /// 有暂停项时，在消息末尾追加一条 todo_paused 提醒。同一轮只在 step 0 调用。
    /// </summary>
    private async Task AppendPausedTodoReminderAsync(List<ChatMessage> history, AgentContext context)
    {
        if (!HasTodoWriteTool(context)
            || _todoStore == null
            || string.IsNullOrEmpty(context.SessionId))
            return;

        var todoList = await LoadTodoList(context.SessionId).ConfigureAwait(false);
        if (!todoList.HasPaused())
            return;

        var reminder = BuildTodoReminderMessage(
            SystemReminder.Kinds.TodoPaused,
            todoList.FormatBrief());
        if (reminder != null)
            history.Add(reminder);
    }

    /// <summary>
    /// 仍有 Pending / InProgress 时追加一条 todo_incomplete 并要求再跑一轮。
    /// 每轮循环最多拦截一次。
    /// </summary>
    /// <returns>已拦截并应 continue 时为 true。</returns>
    private async Task<bool> HoldForIncompleteTodosAsync(
        List<ChatMessage> history,
        AgentContext context,
        LoopExecutionState state)
    {
        if (state.IncompleteReminded
            || !HasTodoWriteTool(context)
            || _todoStore == null
            || string.IsNullOrEmpty(context.SessionId))
            return false;

        var todoList = await LoadTodoList(context.SessionId).ConfigureAwait(false);
        if (!todoList.HasIncompletePendingOrInProgress())
            return false;

        var reminder = BuildTodoReminderMessage(
            SystemReminder.Kinds.TodoIncomplete,
            todoList.FormatBrief());
        if (reminder == null)
            return false;

        history.Add(reminder);
        state.IncompleteReminded = true;
        return true;
    }

    /// <summary>
    /// 从会话加载 Todo 列表（经 ITodoStore 端口，不再直读 Session 键值）
    /// </summary>
    private async Task<TodoList> LoadTodoList(string sessionId)
    {
        if (_todoStore == null)
            return new TodoList { SessionId = sessionId, Items = new() };

        return await _todoStore.LoadAsync(sessionId);
    }

    /// <summary>
    /// 构建 Todo 提醒消息
    /// </summary>
    private static ChatMessage? BuildTodoReminderMessage(
        string kind, string taskBody)
    {
        var content = SystemReminderRenderer.Wrap(
            taskBody,
            SystemReminder.Sources.Agent,
            kind);
        return new ChatMessage { Role = ChatRole.User, Content = content };
    }
}

/// <summary>
/// 单次 Agent Loop 执行的可变状态（per-execution）。
/// <para>
/// AgentExecutor 注册为 Singleton，所有执行期可变字段必须挂在其实例上并按 Loop 创建，
/// 否则并发会话会互相踩踏（X5）。同时作为循环检测器（LoopDetector）的 per-Loop 状态挂点。
/// </para>
/// </summary>
internal sealed class LoopExecutionState
{
    /// <summary>是否已发送过「未完成 Todo」提醒。</summary>
    public bool IncompleteReminded { get; set; }

    /// <summary>本 Loop 专用的循环检测器（参数 SHA256 哈希，连续 3 次警告、5 次终止）。</summary>
    public LoopDetector LoopDetector { get; } = new(threshold: 3);

    /// <summary>是否已请求终止本 Loop（检测到 Doom Loop）。</summary>
    public bool TerminateRequested { get; set; }

    /// <summary>循环检测终止原因（<see cref="TerminateRequested"/> 为 true 时填充）。</summary>
    public string? TerminationReason { get; set; }
}
