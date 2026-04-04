using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Llm;
using Seeing.Agent.Rules;
using Seeing.Agent.Skills;
using Seeing.Agent.Tools;
using ChatMessage = Seeing.Agent.Llm.ChatMessage;
using ChatRole = Seeing.Agent.Llm.ChatRole;
using LlmToolCall = Seeing.Agent.Llm.ToolCall;

namespace Seeing.Agent.Tui.Services;

/// <summary>
/// 多轮 LLM + ToolInvoker 编排，含 RuleEngine 工具门禁。
/// </summary>
public sealed class ChatOrchestrator
{
    private readonly ILlmService _llm;
    private readonly ToolInvoker _tools;
    private readonly RuleEngine _rules;
    private readonly IPermissionChannel _permission;
    private readonly IOptions<SeeingAgentOptions> _options;
    private readonly SkillManager _skills;
    private readonly TuiHostState _host;
    private readonly ILogger<ChatOrchestrator> _logger;

    public ChatOrchestrator(
        ILlmService llm,
        ToolInvoker tools,
        RuleEngine rules,
        IPermissionChannel permission,
        IOptions<SeeingAgentOptions> options,
        SkillManager skills,
        TuiHostState host,
        ILogger<ChatOrchestrator> logger)
    {
        _llm = llm;
        _tools = tools;
        _rules = rules;
        _permission = permission;
        _options = options;
        _skills = skills;
        _host = host;
        _logger = logger;
    }

    public async Task RunTurnAsync(List<ChatMessage> history, string userContent, Action<string> onAssistantChunk, CancellationToken cancellationToken)
    {
        history.Add(new ChatMessage { Role = ChatRole.User, Content = userContent });

        var agent = ResolveAgentConfig();
        var modelId = !string.IsNullOrEmpty(agent.Model)
            ? agent.Model!
            : _options.Value.DefaultModel ?? throw new InvalidOperationException("未配置模型：请在 seeing.json / appsettings 中设置 DefaultModel 或 Agents.*.Model");

        var maxSteps = agent.MaxSteps ?? 32;
        var toolSchemas = _tools.GetToolSchemas();
        var llmTools = MapToLlmTools(toolSchemas);

        for (var step = 0; step < maxSteps; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var request = new ChatRequest
            {
                Messages = new List<ChatMessage>(history),
                SystemPrompt = BuildSystemPrompt(agent),
                Tools = llmTools.Count > 0 ? llmTools : null,
                Temperature = agent.Temperature,
                MaxTokens = agent.MaxTokens
            };

            ChatMessage assistantMsg;
            try
            {
                assistantMsg = await StreamAssistantMessageAsync(
                    modelId,
                    request,
                    onAssistantChunk,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LLM 流式调用失败");
                onAssistantChunk($"\n[错误] {ex.Message}\n");
                return;
            }

            history.Add(assistantMsg);

            if (!string.IsNullOrEmpty(assistantMsg.Content) && assistantMsg.ToolCalls is { Count: > 0 })
                onAssistantChunk("\n");

            var toolCalls = assistantMsg.ToolCalls;
            if (toolCalls == null || toolCalls.Count == 0)
                return;

            foreach (var tc in toolCalls)
            {
                var name = tc.Function?.Name ?? "";
                if (string.IsNullOrEmpty(name))
                    continue;

                var decision = _rules.EvaluateTool(name, null);
                if (decision.Action == PermissionAction.Deny)
                {
                    history.Add(new ChatMessage
                    {
                        Role = ChatRole.Tool,
                        ToolCallId = tc.Id,
                        Content = $"[已拒绝] {decision.Reason ?? "权限规则拒绝"}"
                    });
                    continue;
                }

                if (decision.Action == PermissionAction.Ask)
                {
                    var approved = await _permission.RequestConfirmationAsync(new PermissionRequest
                    {
                        Permission = "tool",
                        Patterns = new List<string> { name },
                        Metadata = new Dictionary<string, object> { ["reason"] = decision.Reason ?? "" }
                    });
                    if (!approved)
                    {
                        history.Add(new ChatMessage
                        {
                            Role = ChatRole.Tool,
                            ToolCallId = tc.Id,
                            Content = "[用户拒绝] 工具调用未执行"
                        });
                        continue;
                    }
                }

                Core.Models.ToolCall coreCall;
                try
                {
                    var argsJson = tc.Function?.Arguments ?? "{}";
                    using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
                    coreCall = new Core.Models.ToolCall
                    {
                        Id = tc.Id,
                        Name = name,
                        Arguments = doc.RootElement.Clone()
                    };
                }
                catch (Exception ex)
                {
                    history.Add(new ChatMessage
                    {
                        Role = ChatRole.Tool,
                        ToolCallId = tc.Id,
                        Content = $"[参数错误] {ex.Message}"
                    });
                    continue;
                }

                ToolCallResult execResult;
                try
                {
                    execResult = await _tools.ExecuteAsync(coreCall, _host.SessionId, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "工具执行异常: {Tool}", name);
                    history.Add(new ChatMessage
                    {
                        Role = ChatRole.Tool,
                        ToolCallId = tc.Id,
                        Content = $"[异常] {ex.Message}"
                    });
                    continue;
                }

                var outText = execResult.Success
                    ? execResult.CallResult?.ToString() ?? ""
                    : execResult.Message?.ToString() ?? execResult.CallResult?.ToString() ?? "失败";
                history.Add(new ChatMessage
                {
                    Role = ChatRole.Tool,
                    ToolCallId = tc.Id,
                    Content = string.IsNullOrEmpty(outText) ? "(空结果)" : outText
                });
            }
        }

        onAssistantChunk($"\n[达到最大步数 {maxSteps}，已停止]\n");
    }

    /// <summary>
    /// 使用 <see cref="ILlmService.CompleteStreamAsync"/> 聚合助手消息（正文 + 工具调用），并增量回调 UI。
    /// </summary>
    private async Task<ChatMessage> StreamAssistantMessageAsync(
        string modelId,
        ChatRequest request,
        Action<string> onAssistantChunk,
        CancellationToken cancellationToken)
    {
        var contentSb = new StringBuilder();
        List<LlmToolCall>? finalTools = null;

        await foreach (var update in _llm.CompleteStreamAsync(modelId, request, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!string.IsNullOrEmpty(update.ReasoningDelta))
                onAssistantChunk(update.ReasoningDelta);

            if (!string.IsNullOrEmpty(update.ContentDelta))
            {
                if (!update.IsComplete)
                {
                    contentSb.Append(update.ContentDelta);
                    onAssistantChunk(update.ContentDelta);
                }
                else if (contentSb.Length == 0)
                {
                    contentSb.Append(update.ContentDelta);
                    onAssistantChunk(update.ContentDelta);
                }
            }

            if (update.IsComplete && update.ToolCallDeltas is { Count: > 0 } t)
                finalTools = t;
        }

        return new ChatMessage
        {
            Role = ChatRole.Assistant,
            Content = contentSb.ToString(),
            ToolCalls = finalTools
        };
    }

    private AgentConfig ResolveAgentConfig()
    {
        var agents = _options.Value.Agents;
        var key = _host.CurrentAgentKey;
        if (!string.IsNullOrEmpty(key) && agents.TryGetValue(key, out var cfg) && cfg != null)
            return cfg;

        if (agents.Count > 0)
            return agents.Values.First();

        return new AgentConfig();
    }

    private string BuildSystemPrompt(AgentConfig agent)
    {
        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrEmpty(agent.SystemPrompt))
        {
            sb.AppendLine(agent.SystemPrompt.Trim());
            sb.AppendLine();
        }

        sb.AppendLine($"工作区目录: {_host.WorkspaceRoot}");

        var skillInfos = _skills.GetAllSkillInfos();
        if (skillInfos.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## 可用 Skill（SKILL.md 元数据，可按名称委派）");
            foreach (var kv in skillInfos.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine($"- **{kv.Key}**: {kv.Value.Description}");
        }

        if (!string.IsNullOrWhiteSpace(_host.RulesMarkdown))
        {
            sb.AppendLine();
            sb.AppendLine(_host.RulesMarkdown);
        }

        return sb.ToString().Trim();
    }

    private static List<Seeing.Agent.Llm.ToolDefinition> MapToLlmTools(List<FunctionToolSchema> schemas)
    {
        var list = new List<Seeing.Agent.Llm.ToolDefinition>(schemas.Count);
        foreach (var s in schemas)
        {
            if (s.Function == null)
                continue;
            object? parameters = new { };
            if (s.Function.Parameters is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } p)
                parameters = p;

            list.Add(new Seeing.Agent.Llm.ToolDefinition
            {
                Type = "function",
                Function = new Seeing.Agent.Llm.FunctionDefinition
                {
                    Name = s.Function.Name,
                    Description = s.Function.Description,
                    Parameters = parameters
                }
            });
        }

        return list;
    }
}
