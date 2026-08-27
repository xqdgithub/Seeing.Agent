using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Abstractions.Agents;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Configuration;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Core.Permission;
using Seeing.Agent.Core.Todo;
using Seeing.Agent.Core.Prompts;
using Seeing.Agent.Core.Reminders;
using Seeing.Agent.Abstractions.Todo;
using Seeing.Agent.Llm;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Tools;
using Seeing.Agent.Tools.BuiltIn.Todo;

using Seeing.Agent.Abstractions.Permissions;
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
    private readonly Scheduling.IAgentLoopScheduler? _loopScheduler;
    private readonly ITodoStore? _todoStore;
    private bool _todoEmptyReminded;
    private bool _incompleteReminded;
    private int _totalToolCallsExecuted;

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
        Scheduling.IAgentLoopScheduler? loopScheduler = null,
        ITodoStore? todoStore = null)
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
        _loopScheduler = loopScheduler;
        _todoStore = todoStore;
    }

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

        // AgentExecutor 为 Singleton：每次执行重置 Loop 局部状态，避免跨执行泄漏
        _todoEmptyReminded = false;
        _incompleteReminded = false;
        _totalToolCallsExecuted = 0;

        // 事件流水线：生产者保证「LoopStart → 恰一个终态事件」；取消/异常在生产者侧转换为终态事件。
        // 与 ACP 执行器（EventYieldingSink + runTask）同一模式，消费侧只需正常读取。
        var outbound = Channel.CreateUnbounded<IMessageEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        var producer = RunProducerAsync(agent, messages, context, effectiveToken, outbound.Writer);

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
        ChannelWriter<IMessageEvent> writer)
    {
        // ========== 生成 LoopId（一次完整对话循环的唯一标识）==========
        var loopId = Guid.NewGuid().ToString("N");
        var loopStartTime = DateTime.Now;
        var totalSteps = 0;
        TokenUsage? totalUsage = null;
        string? errorMessage = null;

        var maxSteps = agent.MaxSteps ?? 32;
        var history = messages.ToList();

        // Ask 全局串行：并行工具不得并发弹出多个 Ask
        var permissionChannel = context.PermissionChannel
            ?? Seeing.Agent.Core.Permission.DefaultPermissionChannel.Instance;

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

                // === 循环开始检查：上一轮遗留的 paused todo ===
                if (step == 0 && _todoStore != null && !string.IsNullOrEmpty(context.SessionId))
                {
                    var todoList = await LoadTodoList(context.SessionId);
                    if (todoList.HasPaused())
                    {
                        var reminder = BuildTodoReminderMessage(
                            SystemReminder.Kinds.TodoPaused,
                            todoList.FormatBrief());
                        if (reminder != null)
                            history.Add(reminder);
                    }
                }

                // ========== 发布 StreamStart 事件（标记新轮次开始）==========
                await writer.WriteAsync(new StreamStartEvent
                {
                    SessionId = context.SessionId,
                    LoopId = loopId,
                    Step = step
                }, effectiveToken);

                // === TodoEmpty 检查：已执行多步但未创建 todo ===
                if (step >= 2 && !_todoEmptyReminded
                    && _todoStore != null && !string.IsNullOrEmpty(context.SessionId))
                {
                    var todoList = await LoadTodoList(context.SessionId);
                    if (todoList.IsEmpty())
                    {
                        var reminder = BuildTodoReminderMessage(
                            SystemReminder.Kinds.TodoEmpty,
                            "请评估当前任务是否需要使用 TodoWrite 规划。");
                        if (reminder != null)
                            history.Add(reminder);
                        _todoEmptyReminded = true;
                    }
                }

                // 构建请求（异步注入动态上下文；token 改用 effectiveToken）
                var request = await BuildRequestAsync(agent, history, context, effectiveToken).ConfigureAwait(false);

                // 调用 LLM（流式）
                ChatMessage? assistantMessage = null;
                var streamingContent = new StringBuilder();
                var streamingReasoning = new StringBuilder();
                List<ToolCall>? accumulatedToolCalls = null;
                TokenUsage? lastUsage = null;

                // 使用 Channel 模式捕获 LLM 流式异常
                var llmChannel = System.Threading.Channels.Channel.CreateUnbounded<StreamUpdate>();
                Exception? llmException = null;

                var llmTask = Task.Run(async () =>
                {
                    try
                    {
                        await foreach (var update in _llm.CompleteStreamAsync(
                            ResolveModelId(agent, context),
                            request,
                            context.SessionId,
                            effectiveToken))
                        {
                            await llmChannel.Writer.WriteAsync(update, effectiveToken).ConfigureAwait(false);
                        }
                        llmChannel.Writer.Complete();
                    }
                    catch (OperationCanceledException ex) when (ex.CancellationToken == effectiveToken)
                    {
                        // 用户主动取消，不作为错误
                        _logger.LogInformation("[AgentExecutor] LLM 请求被取消");
                        llmChannel.Writer.Complete();
                    }
                    catch (LlmRetryExhaustedException ex)
                    {
                        llmException = ex;
                        _logger.LogError(ex, "[AgentExecutor] LLM 重试耗尽: {Message}", ex.Message);
                        llmChannel.Writer.Complete(ex);
                    }
                    catch (LlmTimeoutException ex)
                    {
                        llmException = ex;
                        _logger.LogError(ex, "[AgentExecutor] LLM 请求超时");
                        llmChannel.Writer.Complete(ex);
                    }
                    catch (LlmStreamingException ex)
                    {
                        llmException = ex;
                        _logger.LogError(ex, "[AgentExecutor] LLM 流式错误: {Message}", ex.Message);
                        llmChannel.Writer.Complete(ex);
                    }
                    catch (LlmException ex)
                    {
                        llmException = ex;
                        _logger.LogError(ex, "[AgentExecutor] LLM 错误: {Message}", ex.Message);
                        llmChannel.Writer.Complete(ex);
                    }
                    catch (IOException ex)
                    {
                        llmException = new LlmConnectionException("网络连接错误", ex);
                        _logger.LogError(ex, "[AgentExecutor] 网络连接错误");
                        llmChannel.Writer.Complete(llmException);
                    }
                    catch (Exception ex)
                    {
                        llmException = new LlmException($"LLM 调用失败: {ex.Message}", ex);
                        _logger.LogError(ex, "[AgentExecutor] LLM 调用失败");
                        llmChannel.Writer.Complete(llmException);
                    }
                }, effectiveToken);

                // 从 channel 读取流式更新
                await foreach (var update in llmChannel.Reader.ReadAllAsync(effectiveToken))
                {
                    // ========== 处理思考过程增量 ==========
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

                    // ========== 处理正文内容增量 ==========
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

                    // 累积工具调用
                    if (update.ToolCallDeltas != null && update.ToolCallDeltas.Count > 0)
                    {
                        accumulatedToolCalls ??= new List<ToolCall>();
                        accumulatedToolCalls.AddRange(update.ToolCallDeltas);
                    }

                    // 记录 Usage
                    if (update.Usage != null)
                    {
                        lastUsage = update.Usage;
                        if (totalUsage == null)
                        {
                            totalUsage = new TokenUsage
                            {
                                InputTokens = update.Usage.InputTokens,
                                OutputTokens = update.Usage.OutputTokens
                            };
                        }
                        else
                        {
                            totalUsage = totalUsage with
                            {
                                InputTokens = totalUsage.InputTokens + update.Usage.InputTokens,
                                OutputTokens = totalUsage.OutputTokens + update.Usage.OutputTokens
                            };
                        }
                    }

                    if (update.IsComplete)
                    {
                        assistantMessage = BuildAssistantMessage(
                            update,
                            streamingContent.ToString(),
                            streamingReasoning.ToString(),
                            accumulatedToolCalls);
                    }
                }

                // 确保后台任务完成
                await llmTask.ConfigureAwait(false);

                // 取消优先：取消后不再把「空响应」误报为错误，交由外层 catch 发布 LoopCancelledEvent
                if (effectiveToken.IsCancellationRequested)
                    throw new OperationCanceledException(effectiveToken);

                // 处理 LLM 异常
                if (llmException != null)
                {
                    errorMessage = llmException.Message;

                    _hooks.TriggerFireAndForget(
                        HookRegistry.ChatOnError,
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

                // 无工具调用则检查 todo 状态
                if (assistantMessage.ToolCalls == null || assistantMessage.ToolCalls.Count == 0)
                {
                    // === 退出检查：未完成 todo ===
                    if (_todoStore != null && !string.IsNullOrEmpty(context.SessionId))
                    {
                        var todoList = await LoadTodoList(context.SessionId);
                        if (todoList.HasIncompletePendingOrInProgress())
                        {
                            if (!_incompleteReminded)
                            {
                                var reminder = BuildTodoReminderMessage(
                                    SystemReminder.Kinds.TodoIncomplete,
                                    todoList.FormatBrief());
                                if (reminder != null)
                                    history.Add(reminder);
                                _incompleteReminded = true;
                                continue; // 不退出，再给一轮
                            }
                            // 第二次仍然未完成 → 信任 agent，允许退出
                        }
                    }

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

                // 累计工具调用次数
                if (assistantMessage.ToolCalls != null)
                    _totalToolCallsExecuted += assistantMessage.ToolCalls.Count;

                // ========== 执行工具调用（内部排空，保证终态送达）==========
                await ExecuteToolCallsAsync(
                    assistantMessage.ToolCalls ?? new List<ToolCall>(),
                    agent,
                    context,
                    permissionChannel,
                    loopId,
                    writer,
                    history,
                    effectiveToken);
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
        var toolSchemas = GetToolSchemas(agent);

        // 使用 PromptBuilder 构建系统提示词
        string? systemPrompt = null;
        if (!string.IsNullOrEmpty(agent.SystemPrompt))
        {
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
            };
            systemPrompt = await _promptBuilder.BuildAsync(promptContext, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(systemPrompt))
                systemPrompt = null;
        }

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
            Stream = true
        };
    }

    /// <summary>
    /// 工具终态事件排空时限（仅取消路径兜底）：防止个别不响应取消的工具无限阻塞 Loop 终态。
    /// 正常路径不设超时。测试可通过 internal setter 缩短以加速回归。
    /// </summary>
    internal static TimeSpan ToolDrainTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 执行工具调用 - 直接写入事件流。
    /// 关键：消费端用 CancellationToken.None 排空，取消后工具终态事件（Cancelled/Success/Failed）完整送达；
    /// 排空受 ToolDrainTimeout 保护，避免被不响应取消的工具无限阻塞。
    /// </summary>
    private async Task ExecuteToolCallsAsync(
        List<ToolCall> toolCalls,
        Seeing.Agent.Abstractions.Agents.AgentDefinition agent,
        AgentContext context,
        IPermissionChannel permissionChannel,
        string loopId,
        ChannelWriter<IMessageEvent> writer,
        List<ChatMessage> history,
        CancellationToken cancellationToken)
    {
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
            tc, agent, context, permissionChannel, loopId, channel.Writer, cancellationToken)).ToList();

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

        if (cancellationToken.IsCancellationRequested)
        {
            // 取消路径：用有限排空时间兜底，避免被不响应取消的工具无限阻塞终态。
            var drain = Task.WhenAll(forward, allDone);
            var winner = await Task.WhenAny(drain, Task.Delay(ToolDrainTimeout, CancellationToken.None));
            if (!ReferenceEquals(winner, drain))
            {
                _logger.LogWarning(
                    "[AgentExecutor] 工具终态事件排空超时({TimeoutSeconds}s)，跳过剩余工具事件",
                    ToolDrainTimeout.TotalSeconds);
                channel.Writer.TryComplete();
                try { await forward.ConfigureAwait(false); } catch { /* 个别事件丢弃为已知边界 */ }
                return; // 不等待不响应取消的工具（任务泄漏为已知边界，与修复前一致）
            }
            // 排空成功：forward 与 allDone 均已结束
        }
        else
        {
            // 正常路径无超时（与修复前一致）：等全部工具完成并转发完毕，
            // 保证 tool 历史完整写入，避免下一轮 LLM 因缺 tool 消息报 400。
            // RunToolCallWithEventsAsync 内部已捕获 OCE/异常，allDone 不会 fault。
            await allDone.ConfigureAwait(false);
            await forward.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 转发工具事件并同步追加 tool 历史（所有终态都必须有对应的 tool 消息，否则下一轮 LLM 报 400）。
    /// 转发本身不因取消中断：消费端统一用 CancellationToken.None 排空。
    /// </summary>
    private static async Task ForwardToolEventsAsync(
        ChannelReader<IMessageEvent> reader,
        ChannelWriter<IMessageEvent> outbound,
        List<ChatMessage> history)
    {
        await foreach (var evt in reader.ReadAllAsync(CancellationToken.None))
        {
            await outbound.WriteAsync(evt, CancellationToken.None);
            if (evt is ToolCallEvent tcEvent &&
                tcEvent.Status is ToolCallStatus.Success or ToolCallStatus.Failed
                    or ToolCallStatus.Rejected or ToolCallStatus.Cancelled)
            {
                history.Add(new ChatMessage
                {
                    Role = ChatRole.Tool,
                    ToolCallId = tcEvent.ToolCallId,
                    Content = tcEvent.Output ?? tcEvent.Error ?? string.Empty
                });
            }
        }
    }

    private async Task RunToolCallWithEventsAsync(
        ToolCall tc,
        Seeing.Agent.Abstractions.Agents.AgentDefinition agent,
        AgentContext context,
        IPermissionChannel permissionChannel,
        string loopId,
        ChannelWriter<IMessageEvent> writer,
        CancellationToken cancellationToken)
    {
        var name = tc.Function?.Name ?? "";
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
            complete = await ExecuteSingleToolCallAsync(
                tc, agent, context, permissionChannel, loopId, EmitAsync, cancellationToken)
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

        // 使用 CancellationToken.None 确保终态事件即使在被取消时也能写出
        await writer.WriteAsync(complete, CancellationToken.None).ConfigureAwait(false);
    }

    private static ToolCallEvent BuildTerminalEvent(
        ToolCall tc,
        string name,
        AgentContext context,
        string loopId,
        ToolCallStatus status,
        string error)
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
            Error = error
        };
    }

    /// <summary>
    /// 执行单个工具调用（权限 + 执行），返回 Complete 事件。
    /// </summary>
    private async Task<ToolCallEvent> ExecuteSingleToolCallAsync(
        ToolCall tc,
        Seeing.Agent.Abstractions.Agents.AgentDefinition agent,
        AgentContext context,
        IPermissionChannel permissionChannel,
        string loopId,
        Func<IMessageEvent, ValueTask>? emitAsync,
        CancellationToken cancellationToken)
    {
        var name = tc.Function?.Name ?? "";
        var arguments = ParseArguments(tc.Function?.Arguments);
        var startTime = DateTime.Now;

        // Session-first：task 走正常 ToolManager 路径（TaskTool），不再旁路

        var decision = await EvaluatePermissionAsync(name, arguments, agent, context, permissionChannel).ConfigureAwait(false);

        if (decision.Action == PermissionAction.Deny)
        {
            return new ToolCallEvent
            {
                SessionId = context.SessionId,
                LoopId = loopId,
                Type = MessageEventType.ToolCallComplete,
                ToolCallId = tc.Id,
                ToolName = name,
                Arguments = arguments,
                Status = ToolCallStatus.Rejected,
                Error = decision.Reason ?? "权限拒绝",
                Duration = DateTime.Now - startTime
            };
        }


        ToolResult result;
        // 工具执行的全局兜底超时已下沉到 ToolTimeoutDecorator（工具执行漏斗内，能力感知 + 全局配置热重载）。
        // 此处仅处理取消：Loop 取消令牌贯通工具执行，工具抛 OCE 时统一报告"已取消"。
        try
        {
            result = await _tools.ExecuteAsync(
                tc, context.SessionId, cancellationToken, emitAsync, permissionChannel).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ToolCallEvent
            {
                SessionId = context.SessionId,
                LoopId = loopId,
                Type = MessageEventType.ToolCallComplete,
                ToolCallId = tc.Id,
                ToolName = name,
                Arguments = arguments,
                Status = ToolCallStatus.Cancelled,
                Error = "已取消",
                Duration = DateTime.Now - startTime
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AgentExecutor] 工具执行异常: {ToolName}", name);

            return new ToolCallEvent
            {
                SessionId = context.SessionId,
                LoopId = loopId,
                Type = MessageEventType.ToolCallComplete,
                ToolCallId = tc.Id,
                ToolName = name,
                Arguments = arguments,
                Status = ToolCallStatus.Failed,
                Error = ex.Message,
                Duration = DateTime.Now - startTime
            };
        }

        if (!result.Success)
        {
            // 取消优先于失败：工具内部吞掉 OCE 返回 Failure（如 TaskTool 子任务取消）时，
            // 只要本 Loop 已请求取消，终态就应标记为 Cancelled，避免 UI 显示为 Failed。
            if (cancellationToken.IsCancellationRequested)
            {
                return new ToolCallEvent
                {
                    SessionId = context.SessionId,
                    LoopId = loopId,
                    Type = MessageEventType.ToolCallComplete,
                    ToolCallId = tc.Id,
                    ToolName = name,
                    Arguments = arguments,
                    Status = ToolCallStatus.Cancelled,
                    Error = result.Error ?? "已取消",
                    Duration = DateTime.Now - startTime
                };
            }

            return new ToolCallEvent
            {
                SessionId = context.SessionId,
                LoopId = loopId,
                Type = MessageEventType.ToolCallComplete,
                ToolCallId = tc.Id,
                ToolName = name,
                Arguments = arguments,
                Status = ToolCallStatus.Failed,
                Output = result.Output,
                Error = result.Error,
                Duration = DateTime.Now - startTime
            };
        }

        return new ToolCallEvent
        {
            SessionId = context.SessionId,
            LoopId = loopId,
            Type = MessageEventType.ToolCallComplete,
            ToolCallId = tc.Id,
            ToolName = name,
            Arguments = arguments,
            Status = ToolCallStatus.Success,
            Output = result.Output,
            Error = result.Error,
            Duration = DateTime.Now - startTime
        };
    }

    /// <summary>
    /// 评估权限
    /// </summary>
    private async Task<PermissionDecision> EvaluatePermissionAsync(
        string toolName,
        object? arguments,
        Seeing.Agent.Abstractions.Agents.AgentDefinition agent,
        AgentContext context,
        IPermissionChannel permissionChannel)
    {
        var policy = ResolvePolicy(agent, context);
        var permContext = PermissionIntegrity.FromAgentContext(context, policy, agent.Name);

        var result = await _permissions.EvaluateToolAsync(toolName, null, permContext).ConfigureAwait(false);

        if (result.NeedsConfirmation)
        {
            var channelResult = await permissionChannel.RequestAsync(new PermissionRequest
            {
                PermissionKind = "tool.execute",
                Resource = toolName,
                SessionId = context.SessionId,
                Metadata = new Dictionary<string, object>
                {
                    ["arguments"] = arguments ?? new object()
                }
            }, context.CancellationToken).ConfigureAwait(false);

            if (channelResult.Action != PermissionChannelAction.Allow)
                return PermissionDecision.Deny(channelResult.Reason ?? "用户拒绝");

            return PermissionDecision.Allow("用户确认允许");
        }

        return result.ToDecision();
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
            $"Agent '{agent.Name}' 未配置模型，且未设置全局默认模型 (SeeingAgent:DefaultModel)。" +
            $"请在 Agent 定义中设置 Model，或在配置中设置 DefaultModel。");
    }

    /// <summary>
    /// 获取工具 Schema
    /// </summary>
    private List<FunctionToolSchema> GetToolSchemas(Seeing.Agent.Abstractions.Agents.AgentDefinition agent)
    {
        // 基于 Agent 的 Mode 和 Allowed/Denied 列表过滤
        var agentInfo = new AgentDefinition
        {
            Name = agent.Name,
            Mode = agent.Mode,
            AllowedTools = agent.AllowedTools.ToList(),
            DeniedTools = agent.DeniedTools.ToList()
        };

        return _tools.GetToolSchemasForAgent(agentInfo);
    }

    /// <summary>
    /// 构建 Assistant 消息
    /// </summary>
    private static ChatMessage BuildAssistantMessage(
        StreamUpdate update,
        string fullContent,
        string fullReasoning,
        List<ToolCall>? toolCalls)
    {
        return new ChatMessage
        {
            Role = ChatRole.Assistant,
            Content = fullContent,
            ReasoningContent = string.IsNullOrEmpty(fullReasoning) ? null : fullReasoning,
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
