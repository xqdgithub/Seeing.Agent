using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Abstractions;
using Seeing.Agent.Core.Events;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Llm;
using Seeing.Agent.Rules;
using Seeing.Agent.Skills;
using Seeing.Agent.Tui.Core;
using Seeing.Agent.Tui.Core.Models;
using Seeing.Agent.Tui.Integration.Adapters;
using Seeing.Agent.Tui.UI.Renderers;
using ChatMessage = Seeing.Agent.Llm.ChatMessage;
using ChatRole = Seeing.Agent.Llm.ChatRole;
using LlmToolCall = Seeing.Agent.Llm.ToolCall;

namespace Seeing.Agent.Tui.Services;

/// <summary>
/// 聊天编排器 - 统一入口
/// <para>
/// 使用事件驱动架构，通过 IAgentAdapter 订阅事件流并调用 LiveStreamRenderer 渲染。
/// </para>
/// </summary>
public sealed class ChatOrchestrator
{
    private readonly IAgentAdapter _agentAdapter;
    private readonly IAgentRegistry _registry;
    private readonly IOptions<SeeingAgentOptions> _options;
    private readonly IPermissionChannel _permission;
    private readonly SkillManager _skills;
    private readonly TuiState _host;
    private readonly ILogger<ChatOrchestrator> _logger;
    private readonly IServiceProvider _serviceProvider;

    public ChatOrchestrator(
        IAgentAdapter agentAdapter,
        IAgentRegistry registry,
        IOptions<SeeingAgentOptions> options,
        IPermissionChannel permission,
        SkillManager skills,
        TuiState host,
        ILogger<ChatOrchestrator> logger,
        IServiceProvider serviceProvider)
    {
        _agentAdapter = agentAdapter;
        _registry = registry;
        _options = options;
        _permission = permission;
        _skills = skills;
        _host = host;
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// 运行对话轮次 - 事件驱动架构
    /// </summary>
    public async Task RunTurnAsync(
        string userContent,
        CancellationToken cancellationToken)
    {
        var key = _host.CurrentAgentKey;
        var allAgents = await _agentAdapter.GetAgentsAsync();
        var info = allAgents.FirstOrDefault(a => string.Equals(a.Name, key, StringComparison.OrdinalIgnoreCase));
        
        if (info == null)
        {
            _host.AddMessage(new MessageDisplay
            {
                Role = "system",
                Content = $"[red]注册中心中不存在 Agent: {key}[/]",
                Timestamp = DateTime.Now
            });
            return;
        }

        if (info.IsHidden || !CanSelectAsChatAgent(info))
        {
            _host.AddMessage(new MessageDisplay
            {
                Role = "system",
                Content = $"[red]Agent「{info.Name}」当前不可用于对话（已隐藏或模式不允许）。[/]",
                Timestamp = DateTime.Now
            });
            return;
        }

        var instance = _registry.GetOrCreateAgentInstance(info.Name);
        if (instance == null)
        {
            _host.AddMessage(new MessageDisplay
            {
                Role = "system",
                Content = "[red]无法创建 Agent 实例。[/]",
                Timestamp = DateTime.Now
            });
            return;
        }

        // 构建执行上下文
        var context = new AgentContext
        {
            SessionId = _host.SessionId,
            Services = _serviceProvider,
            CancellationToken = cancellationToken,
            PermissionChannel = _permission,
            History = BuildHistory(),
            WorkingDirectory = _host.WorkspaceRoot
        };

        // 添加用户消息
        context.History.Add(new ChatMessage
        {
            Role = ChatRole.User,
            Content = userContent
        });

        // 添加用户消息到 UI
        _host.AddMessage(new MessageDisplay
        {
            Role = "user",
            Content = userContent,
            Timestamp = DateTime.Now
        });

        // 获取 Agent 定义
        var definition = AgentDefinition.FromAgent(instance);

        // 创建流式渲染器
        var renderer = new LiveStreamRenderer(_host);

        try
        {
            // 通过适配器订阅事件流并处理
            await foreach (var evt in _agentAdapter.ExecuteStreamAsync(definition, context, cancellationToken))
            {
                switch (evt.Type)
                {
                    case MessageEventType.StreamDelta:
                        await renderer.HandleDeltaAsync((StreamDeltaEvent)evt);
                        break;
                    
                    case MessageEventType.StreamComplete:
                        await renderer.HandleCompleteAsync((StreamCompleteEvent)evt);
                        break;
                    
                    case MessageEventType.ToolCallPending:
                    case MessageEventType.ToolCallRunning:
                    case MessageEventType.ToolCallComplete:
                        await renderer.HandleToolCallAsync((ToolCallEvent)evt);
                        break;
                    
                    case MessageEventType.SubAgentStarted:
                    case MessageEventType.SubAgentCompleted:
                        await renderer.HandleSubAgentAsync((SubAgentEvent)evt);
                        break;
                    
                    case MessageEventType.Error:
                        await renderer.HandleErrorAsync((ErrorEvent)evt);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            _host.AddMessage(new MessageDisplay
            {
                Role = "system",
                Content = "[yellow]操作已取消[/]",
                Timestamp = DateTime.Now
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent 执行失败: {Name}", definition.Name);
            _host.AddMessage(new MessageDisplay
            {
                Role = "system",
                Content = $"[red]Agent 执行错误: {ex.Message}[/]",
                Timestamp = DateTime.Now
            });
        }
    }

    private static bool CanSelectAsChatAgent(AgentInfo info) =>
        info.Mode is AgentMode.Primary or AgentMode.All or AgentMode.SubAgent;

    private List<ChatMessage> BuildHistory()
    {
        var history = new List<ChatMessage>();

        foreach (var msg in _host.Messages)
        {
            if (msg.Role == "user")
            {
                history.Add(new ChatMessage { Role = ChatRole.User, Content = msg.Content });
            }
            else if (msg.Role == "assistant")
            {
                history.Add(new ChatMessage
                {
                    Role = ChatRole.Assistant,
                    Content = msg.Content,
                    ToolCalls = msg.ToolCalls.Count > 0
                        ? msg.ToolCalls.Select(tc => new LlmToolCall
                        {
                            Id = tc.Id,
                            Function = new FunctionCall
                            {
                                Name = tc.Name,
                                Arguments = tc.Arguments
                            }
                        }).ToList()
                        : null
                });
            }
            else if (msg.Role == "tool")
            {
                history.Add(new ChatMessage
                {
                    Role = ChatRole.Tool,
                    ToolCallId = msg.ToolCallId,
                    Content = msg.ToolResult ?? ""
                });
            }
        }

        return history;
    }
}