using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Events;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Core.Permission;
using Seeing.Agent.Llm;
using Seeing.Agent.Rules;
using Seeing.Agent.Tools;

namespace Seeing.Agent.Core;

/// <summary>
/// Agent 执行器 - 统一执行引擎
/// <para>
/// 参考 oh-my-openagent 设计，提供统一的 LLM 循环和工具调用处理。
/// 支持多入口（TUI/API/CLI）和子代理调用。
/// </para>
/// <para>
/// 返回事件流（IMessageEvent），由调用方决定如何渲染和存储。
/// </para>
/// </summary>
public class AgentExecutor
{
    private readonly ILlmService _llm;
    private readonly ToolInvoker _tools;
    private readonly IRuleEngine _rules;
    private readonly IPermissionService _permissions;
    private readonly IHookManager _hooks;
    private readonly IAgentRegistry _registry;
    private readonly SeeingAgentOptions _options;
    private readonly ILogger<AgentExecutor> _logger;

    public AgentExecutor(
        ILlmService llm,
        ToolInvoker tools,
        IRuleEngine rules,
        IPermissionService permissions,
        IHookManager hooks,
        IAgentRegistry registry,
        IOptions<SeeingAgentOptions> options,
        ILogger<AgentExecutor> logger)
    {
        _llm = llm;
        _tools = tools;
        _rules = rules;
        _permissions = permissions;
        _hooks = hooks;
        _registry = registry;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// 执行 Agent - 核心 LLM 循环
    /// </summary>
    /// <param name="agent">Agent 定义</param>
    /// <param name="context">执行上下文</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>事件流（流式增量、完整消息、工具调用、错误等）</returns>
    public async IAsyncEnumerable<IMessageEvent> ExecuteAsync(
        AgentDefinition agent,
        AgentContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // 合并取消令牌
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            context.CancellationToken, cancellationToken);
        var effectiveToken = linkedCts.Token;

        var maxSteps = agent.MaxSteps ?? 32;
        var messages = context.History.ToList();

        // 获取权限通道（默认允许所有）
        var permissionChannel = context.PermissionChannel 
            ?? Interfaces.DefaultPermissionChannel.Instance;

        for (var step = 0; step < maxSteps; step++)
        {
            effectiveToken.ThrowIfCancellationRequested();

            // ========== 发布 StreamStart 事件（标记新轮次开始）==========
            yield return new StreamStartEvent
            {
                SessionId = context.SessionId,
                Step = step
            };

            // 构建请求
            var request = BuildRequest(agent, messages, context);

            // 调用 LLM（流式）
            ChatMessage? assistantMessage = null;
            var streamingContent = new StringBuilder();
            var streamingReasoning = new StringBuilder();
            List<ToolCall>? accumulatedToolCalls = null;
            TokenUsage? lastUsage = null;

            await foreach (var update in _llm.CompleteStreamAsync(
                ResolveModelId(agent),
                request,
                context.SessionId,
                effectiveToken))
            {
                // ========== 处理思考过程增量 ==========
                if (!string.IsNullOrEmpty(update.ReasoningDelta))
                {
                    streamingReasoning.Append(update.ReasoningDelta);
                    
                    yield return new StreamDeltaEvent
                    {
                        SessionId = context.SessionId,
                        ReasoningDelta = update.ReasoningDelta
                    };
                }

                // ========== 处理正文内容增量 ==========
                if (!string.IsNullOrEmpty(update.ContentDelta))
                {
                    streamingContent.Append(update.ContentDelta);

                    yield return new StreamDeltaEvent
                    {
                        SessionId = context.SessionId,
                        ContentDelta = update.ContentDelta
                    };
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

            if (assistantMessage == null)
            {
                _logger.LogWarning("[AgentExecutor] LLM 返回空响应");
                
                yield return new ErrorEvent
                {
                    SessionId = context.SessionId,
                    Message = "LLM 返回空响应",
                    Source = "llm"
                };
                yield break;
            }

            messages.Add(assistantMessage);

            // ========== 发布 StreamComplete 事件 ==========
            yield return new StreamCompleteEvent
            {
                SessionId = context.SessionId,
                Message = assistantMessage,
                Usage = lastUsage
            };

            // 无工具调用则结束
            if (assistantMessage.ToolCalls == null || assistantMessage.ToolCalls.Count == 0)
            {
                yield break;
            }

            // ========== 执行工具调用 ==========
            await foreach (var toolEvent in ExecuteToolCallsAsync(
                assistantMessage.ToolCalls,
                agent,
                context,
                permissionChannel,
                effectiveToken))
            {
                yield return toolEvent;
                
                // 将工具结果添加到消息历史
                if (toolEvent is ToolCallEvent { Status: ToolCallStatus.Success or ToolCallStatus.Failed } tcEvent 
                    && tcEvent.Output != null)
                {
                    messages.Add(new ChatMessage
                    {
                        Role = ChatRole.Tool,
                        ToolCallId = tcEvent.ToolCallId,
                        Content = tcEvent.Output
                    });
                }
            }
        }

        // 达到最大步数
        yield return new ErrorEvent
        {
            SessionId = context.SessionId,
            Message = $"达到最大步数 {maxSteps}，已停止",
            Source = "agent"
        };
    }

    /// <summary>
    /// 构建聊天请求
    /// </summary>
    private ChatRequest BuildRequest(
        AgentDefinition agent,
        List<ChatMessage> messages,
        AgentContext context)
    {
        var systemPrompt = BuildSystemPrompt(agent, context);
        var toolSchemas = GetToolSchemas(agent);

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
    /// 构建系统提示词
    /// </summary>
    private string? BuildSystemPrompt(AgentDefinition agent, AgentContext context)
    {
        var basePrompt = agent.SystemPrompt;

        if (basePrompt == null)
            return null;

        // 动态注入上下文
        return InjectDynamicContext(basePrompt, agent, context);
    }

    /// <summary>
    /// 注入动态上下文到提示词
    /// </summary>
    private string InjectDynamicContext(string prompt, AgentDefinition agent, AgentContext context)
    {
        // 注入可用 Agent 列表
        if (prompt.Contains("{{availableAgents}}"))
        {
            var agents = GetAvailableAgentsAsync().GetAwaiter().GetResult();
            prompt = prompt.Replace("{{availableAgents}}", FormatAvailableAgents(agents));
        }

        // 注入可用工具
        if (prompt.Contains("{{availableTools}}"))
        {
            var tools = GetToolSchemas(agent);
            prompt = prompt.Replace("{{availableTools}}", FormatAvailableTools(tools));
        }

        // 注入工作目录
        if (prompt.Contains("{{workingDirectory}}"))
        {
            prompt = prompt.Replace("{{workingDirectory}}", context.WorkingDirectory);
        }

        // 注入会话 ID
        if (prompt.Contains("{{sessionId}}"))
        {
            prompt = prompt.Replace("{{sessionId}}", context.SessionId);
        }

        return prompt;
    }

    /// <summary>
    /// 执行工具调用 - 返回事件流
    /// </summary>
    private async IAsyncEnumerable<IMessageEvent> ExecuteToolCallsAsync(
        List<ToolCall> toolCalls,
        AgentDefinition agent,
        AgentContext context,
        IPermissionChannel permissionChannel,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // 并行执行多个工具调用，但按顺序返回事件
        var tasks = toolCalls.Select(tc => ExecuteSingleToolCallAsync(
            tc, agent, context, permissionChannel, cancellationToken)).ToList();

        var results = await Task.WhenAll(tasks);

        foreach (var (tc, result) in toolCalls.Zip(results))
        {
            // 发布 Pending 事件
            yield return new ToolCallEvent
            {
                SessionId = context.SessionId,
                Type = MessageEventType.ToolCallPending,
                ToolCallId = tc.Id,
                ToolName = tc.Function?.Name ?? "",
                Arguments = ParseArguments(tc.Function?.Arguments),
                Status = ToolCallStatus.Pending
            };

            // 发布 Running 事件
            yield return new ToolCallEvent
            {
                SessionId = context.SessionId,
                Type = MessageEventType.ToolCallRunning,
                ToolCallId = tc.Id,
                ToolName = tc.Function?.Name ?? "",
                Status = ToolCallStatus.Running
            };

            // 发布 Complete 事件
            yield return result;

            // 发布结果消息
            yield return new StreamCompleteEvent
            {
                SessionId = context.SessionId,
                Message = new ChatMessage
                {
                    Role = ChatRole.Tool,
                    ToolCallId = tc.Id,
                    Content = result.Output ?? result.Error ?? ""
                }
            };
        }
    }

    /// <summary>
    /// 执行单个工具调用
    /// </summary>
    private async Task<ToolCallEvent> ExecuteSingleToolCallAsync(
        ToolCall tc,
        AgentDefinition agent,
        AgentContext context,
        IPermissionChannel permissionChannel,
        CancellationToken cancellationToken)
    {
        var name = tc.Function?.Name ?? "";
        var arguments = ParseArguments(tc.Function?.Arguments);
        var startTime = DateTime.Now;

        // ========== 特殊处理：task 工具（子代理调用） ==========
        if (string.Equals(name, "task", StringComparison.OrdinalIgnoreCase))
        {
            return await HandleSubAgentCallAsync(tc, agent, context, cancellationToken);
        }

        // 权限检查
        var decision = await EvaluatePermissionAsync(name, arguments, agent, context, permissionChannel);

        if (decision.Action == PermissionAction.Deny)
        {
            return new ToolCallEvent
            {
                SessionId = context.SessionId,
                Type = MessageEventType.ToolCallComplete,
                ToolCallId = tc.Id,
                ToolName = name,
                Arguments = arguments,
                Status = ToolCallStatus.Rejected,
                Error = decision.Reason ?? "权限拒绝",
                Duration = DateTime.Now - startTime
            };
        }

        if (decision.Action == PermissionAction.Ask)
        {
            var userDecision = await permissionChannel.RequestToolPermissionAsync(
                name, arguments, context);

            if (userDecision.Action != PermissionAction.Allow)
            {
                return new ToolCallEvent
                {
                    SessionId = context.SessionId,
                    Type = MessageEventType.ToolCallComplete,
                    ToolCallId = tc.Id,
                    ToolName = name,
                    Arguments = arguments,
                    Status = ToolCallStatus.Rejected,
                    Error = "用户拒绝",
                    Duration = DateTime.Now - startTime
                };
            }
        }

        // 执行工具
        ToolResult result;
        try
        {
            result = await _tools.ExecuteAsync(tc, context.SessionId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AgentExecutor] 工具执行异常: {ToolName}", name);
            
            return new ToolCallEvent
            {
                SessionId = context.SessionId,
                Type = MessageEventType.ToolCallComplete,
                ToolCallId = tc.Id,
                ToolName = name,
                Arguments = arguments,
                Status = ToolCallStatus.Failed,
                Error = ex.Message,
                Duration = DateTime.Now - startTime
            };
        }

        return new ToolCallEvent
        {
            SessionId = context.SessionId,
            Type = MessageEventType.ToolCallComplete,
            ToolCallId = tc.Id,
            ToolName = name,
            Arguments = arguments,
            Status = result.Success ? ToolCallStatus.Success : ToolCallStatus.Failed,
            Output = result.Output,
            Error = result.Error,
            Duration = DateTime.Now - startTime
        };
    }

    /// <summary>
    /// 处理子代理调用（task 工具）
    /// </summary>
    private async Task<ToolCallEvent> HandleSubAgentCallAsync(
        ToolCall tc,
        AgentDefinition parentAgent,
        AgentContext parentContext,
        CancellationToken cancellationToken)
    {
        var args = ParseTaskArguments(tc.Function?.Arguments);
        var startTime = DateTime.Now;

        // 解析目标 Agent
        var targetAgentName = args.SubAgentType ?? args.Category ?? "sisyphus-junior";
        var targetAgent = await ResolveSubAgentAsync(targetAgentName);

        if (targetAgent == null)
        {
            // ========== Hook: PermissionDenied（未找到目标 Agent）==========
            _hooks.TriggerFireAndForget(
                HookRegistry.PermissionDenied,
                parentContext.SessionId,
                new Dictionary<string, object?>
                {
                    ["resource"] = targetAgentName,
                    ["reason"] = $"未找到 Agent: {targetAgentName}"
                });

            return new ToolCallEvent
            {
                SessionId = parentContext.SessionId,
                Type = MessageEventType.ToolCallComplete,
                ToolCallId = tc.Id,
                ToolName = "task",
                Status = ToolCallStatus.Failed,
                Error = $"未找到 Agent: {targetAgentName}",
                Duration = DateTime.Now - startTime
            };
        }

        // 创建子会话
        var subSessionId = $"{parentContext.SessionId}:{targetAgentName}:{Guid.NewGuid():N}";

        // ========== Hook: SubAgentStarted（子代理开始前）==========
        _hooks.TriggerFireAndForget(
            HookRegistry.SubagentStarted,
            parentContext.SessionId,
            new Dictionary<string, object?>
            {
                ["parentSessionId"] = parentContext.SessionId,
                ["subSessionId"] = subSessionId,
                ["subAgentName"] = targetAgentName,
                ["prompt"] = args.Prompt
            });

        // 发布子代理启动事件
        var startEvent = new SubAgentEvent
        {
            SessionId = parentContext.SessionId,
            Type = MessageEventType.SubAgentStarted,
            AgentName = targetAgentName,
            Status = "started",
            SubSessionId = subSessionId
        };

        // 创建子代理上下文（传入父代理名称建立权限继承链）
        var subContext = parentContext.CreateSubAgentContext(subSessionId, targetAgent, parentAgent.Name);
        subContext.History.Add(new ChatMessage
        {
            Role = ChatRole.User,
            Content = args.Prompt ?? ""
        });

        // 执行子代理（收集事件但不转发）
        var subResults = new List<ChatMessage>();
        var lastAssistantContent = new StringBuilder();

        try
        {
            await foreach (var evt in ExecuteAsync(targetAgent, subContext, cancellationToken))
            {
                // 收集最后一条助手消息
                if (evt is StreamCompleteEvent complete && complete.Message.Role == ChatRole.Assistant)
                {
                    subResults.Add(complete.Message);
                    lastAssistantContent.Clear();
                    lastAssistantContent.Append(complete.Message.Content);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AgentExecutor] 子代理执行失败: {AgentName}", targetAgentName);

            return new ToolCallEvent
            {
                SessionId = parentContext.SessionId,
                Type = MessageEventType.ToolCallComplete,
                ToolCallId = tc.Id,
                ToolName = "task",
                Status = ToolCallStatus.Failed,
                Error = $"子代理执行失败: {ex.Message}",
                Duration = DateTime.Now - startTime
            };
        }

        // 构建返回结果
        var resultContent = BuildSubAgentResult(lastAssistantContent.ToString(), subSessionId, targetAgentName);

        // ========== Hook: SubAgentCompleted（子代理完成后）==========
        _hooks.TriggerFireAndForget(
            HookRegistry.SubagentCompleted,
            parentContext.SessionId,
            new Dictionary<string, object?>
            {
                ["parentSessionId"] = parentContext.SessionId,
                ["subSessionId"] = subSessionId,
                ["subAgentName"] = targetAgentName,
                ["success"] = true
            });

        return new ToolCallEvent
        {
            SessionId = parentContext.SessionId,
            Type = MessageEventType.ToolCallComplete,
            ToolCallId = tc.Id,
            ToolName = "task",
            Status = ToolCallStatus.Success,
            Output = resultContent,
            Title = "子代理完成",
            Duration = DateTime.Now - startTime
        };
    }

    /// <summary>
    /// 评估权限
    /// </summary>
    private async Task<PermissionDecision> EvaluatePermissionAsync(
        string toolName,
        object? arguments,
        AgentDefinition agent,
        AgentContext context,
        IPermissionChannel permissionChannel)
    {
        // 统一使用 IPermissionService 进行权限检查
        // AllowedTools/DeniedTools 检查已由 PermissionService.EvaluateRulesAsync 内部处理
        var permContext = context.PermissionContext 
            ?? PermissionContext.FromAgentContext(context, agent.BuildPermissionPolicy());
        
        var result = await _permissions.EvaluateToolAsync(toolName, null, permContext);
        
        // 处理 Ask 情况
        if (result.NeedsConfirmation)
        {
            var userDecision = await permissionChannel.RequestToolPermissionAsync(
                toolName, arguments, context);
            
            if (userDecision.Action != PermissionAction.Allow)
            {
                return PermissionDecision.Deny("用户拒绝");
            }
            
            return PermissionDecision.Allow("用户确认允许");
        }
        
        return result.ToDecision();
    }

    /// <summary>
    /// 解析模型 ID
    /// </summary>
    private string ResolveModelId(AgentDefinition agent)
    {
        // 1. 优先使用 Agent 定义的模型
        if (agent.Model != null)
        {
            return agent.Model.ToString();
        }

        // 2. 回退到全局默认模型
        if (!string.IsNullOrEmpty(_options.DefaultModel))
        {
            _logger.LogDebug("[AgentExecutor] Agent {Name} 未配置模型，使用全局默认: {DefaultModel}",
                agent.Name, _options.DefaultModel);
            return _options.DefaultModel;
        }

        // 3. 无默认模型配置，抛出错误
        throw new InvalidOperationException(
            $"Agent '{agent.Name}' 未配置模型，且未设置全局默认模型 (SeeingAgent:DefaultModel)。" +
            $"请在 Agent 定义中设置 Model，或在配置中设置 DefaultModel。");
    }

    /// <summary>
    /// 获取工具 Schema
    /// </summary>
    private List<FunctionToolSchema> GetToolSchemas(AgentDefinition agent)
    {
        // 基于 Agent 的 Mode 和 Allowed/Denied 列表过滤
        var agentInfo = new AgentInfo
        {
            Name = agent.Name,
            Mode = agent.Mode,
            AllowedTools = agent.AllowedTools.ToList(),
            DeniedTools = agent.DeniedTools.ToList()
        };

        return _tools.GetToolSchemasForAgent(agentInfo);
    }

    /// <summary>
    /// 获取可用 Agent 列表
    /// </summary>
    private async Task<List<AgentDefinition>> GetAvailableAgentsAsync()
    {
        var agents = await _registry.GetAgentsAsync();
        return agents
            .Where(a => !a.IsHidden && a.Mode != AgentMode.Primary)
            .Select(a => AgentDefinition.FromAgent(
                _registry.GetOrCreateAgentInstance(a.Name) ?? throw new InvalidOperationException()))
            .ToList();
    }

    /// <summary>
    /// 格式化可用 Agent 列表
    /// </summary>
    private static string FormatAvailableAgents(List<AgentDefinition> agents)
    {
        if (agents.Count == 0)
            return "无可用子代理";

        var sb = new StringBuilder();
        foreach (var agent in agents)
        {
            sb.AppendLine($"- {agent.Name}: {agent.Description}");
            if (!string.IsNullOrEmpty(agent.Category))
            {
                sb.AppendLine($"  类别: {agent.Category}");
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// 格式化可用工具列表
    /// </summary>
    private static string FormatAvailableTools(List<FunctionToolSchema> tools)
    {
        if (tools.Count == 0)
            return "无可用工具";

        var sb = new StringBuilder();
        foreach (var tool in tools)
        {
            sb.AppendLine($"- {tool.Function.Name}: {tool.Function.Description}");
        }
        return sb.ToString();
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
    /// 解析 task 工具参数
    /// </summary>
    private static TaskArguments ParseTaskArguments(string? arguments)
    {
        var result = new TaskArguments();

        if (string.IsNullOrEmpty(arguments))
            return result;

        try
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(arguments);
            if (dict == null)
                return result;

            if (dict.TryGetValue("subagent_type", out var subagentType))
                result.SubAgentType = subagentType?.ToString();

            if (dict.TryGetValue("category", out var category))
                result.Category = category?.ToString();

            if (dict.TryGetValue("prompt", out var prompt))
                result.Prompt = prompt?.ToString();

            if (dict.TryGetValue("description", out var description))
                result.Description = description?.ToString();

            if (dict.TryGetValue("run_in_background", out var background))
                result.RunInBackground = background is bool b && b;

            if (dict.TryGetValue("session_id", out var sessionId))
                result.SessionId = sessionId?.ToString();
        }
        catch (Exception ex)
        {
            // 记录但继续
        }

        return result;
    }

    /// <summary>
    /// 解析子代理
    /// </summary>
    private async Task<AgentDefinition?> ResolveSubAgentAsync(string agentName)
    {
        var agents = await _registry.GetAgentsAsync();
        var info = agents.FirstOrDefault(a =>
            string.Equals(a.Name, agentName, StringComparison.OrdinalIgnoreCase));

        if (info == null)
            return null;

        var instance = _registry.GetOrCreateAgentInstance(info.Name);
        return instance != null ? AgentDefinition.FromAgent(instance) : null;
    }

    /// <summary>
    /// 构建子代理结果
    /// </summary>
    private static string BuildSubAgentResult(
        string content,
        string sessionId,
        string agentName)
    {
        return $"[子代理 {agentName} 完成]\n{content}\n\nSession: {sessionId}";
    }

    /// <summary>
    /// Task 工具参数
    /// </summary>
    private class TaskArguments
    {
        public string? SubAgentType { get; set; }
        public string? Category { get; set; }
        public string? Prompt { get; set; }
        public string? Description { get; set; }
        public bool RunInBackground { get; set; }
        public string? SessionId { get; set; }
    }
}