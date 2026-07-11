using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Seeing.Agent.App.Events;
using Seeing.Agent.App.Internal;
using Seeing.Agent.App.Models;
using Seeing.Agent.Commands;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Events;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Llm;
using Seeing.Session.Core;
using Seeing.Session.Storage;

namespace Seeing.Agent.App;

/// <summary>
/// 聊天编排器 - 统一的 Agent 执行入口
/// <para>
/// 提供最小化的入口参数，内部管理 Session 生命周期、命令预处理、Agent 执行、事件发送。
/// </para>
/// </summary>
public class ChatOrchestrator : IChatOrchestrator
{
    private readonly ISessionManager _sessionManager;
    private readonly ISessionStore _sessionStore;
    private readonly IAgentRegistry _agentRegistry;
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly IAgentExecutionRouter _executionRouter;
    private readonly CommandDispatcher _commandDispatcher;
    private readonly ICommandRegistry _commandRegistry;
    private readonly IPermissionChannel _permissionChannel;
    private readonly AgentSelectionResolver _agentSelectionResolver;
    private readonly ChatExecutionQueue _executionQueue;
    private readonly ChatRunTracker _runTracker;
    private readonly ILogger<ChatOrchestrator> _logger;

    public ChatOrchestrator(
        ISessionManager sessionManager,
        ISessionStore sessionStore,
        IAgentRegistry agentRegistry,
        IWorkspaceProvider workspaceProvider,
        IAgentExecutionRouter executionRouter,
        CommandDispatcher commandDispatcher,
        ICommandRegistry commandRegistry,
        IPermissionChannel permissionChannel,
        AgentSelectionResolver agentSelectionResolver,
        ChatExecutionQueue executionQueue,
        ChatRunTracker runTracker,
        ILogger<ChatOrchestrator> logger)
    {
        _sessionManager = sessionManager;
        _sessionStore = sessionStore;
        _agentRegistry = agentRegistry;
        _workspaceProvider = workspaceProvider;
        _executionRouter = executionRouter;
        _commandDispatcher = commandDispatcher;
        _commandRegistry = commandRegistry;
        _permissionChannel = permissionChannel;
        _agentSelectionResolver = agentSelectionResolver;
        _executionQueue = executionQueue;
        _runTracker = runTracker;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<IMessageEvent> ExecuteAsync(
        string sessionId,
        ChatInput input,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // 1. 确保会话存在
        var session = await _sessionManager.EnsureSessionAsync(sessionId);
        
        // 2. 构建执行上下文
        var context = await BuildExecutionContextAsync(sessionId, input, options, cancellationToken);
        
        // 3. 添加用户消息到 Session
        var userMessage = BuildUserMessage(input);
        session.Messages.Add(userMessage);
        
        // 4. 发送 Session 更新事件
        yield return new SessionUpdatedEvent
        {
            SessionId = sessionId,
            Session = session
        };
        
        // 5. 命令预处理
        if (input.Text != null && CommandDispatcher.IsCommand(input.Text))
        {
            IMessageEvent? lastCmdEvent = null;
            await foreach (var cmdEvent in ProcessCommandAsync(sessionId, input.Text, session, context, cancellationToken))
            {
                if (cmdEvent != null)
                {
                    lastCmdEvent = cmdEvent;
                    yield return cmdEvent;
                }
            }
            
            // 系统命令执行完毕，不继续 Agent 流程
            if (lastCmdEvent is CommandResultEvent)
            {
                yield break;
            }
        }
        
        // 6. 更新 History
        context.History = BuildHistoryFromSession(session);
        
        // 7. 执行 Agent（直接返回 IMessageEvent）
        await foreach (var evt in _executionRouter.ExecuteAsync(context.Agent, BuildAgentContext(context, cancellationToken), cancellationToken))
        {
            yield return evt;
        }
        
        // 8. 保存 Session
        await _sessionManager.SaveAsync(sessionId);
        
        // 9. 发送最终 Session 更新
        yield return new SessionUpdatedEvent
        {
            SessionId = sessionId,
            Session = session
        };
    }

    /// <inheritdoc/>
    public bool Stop(string sessionId)
    {
        return _runTracker.TryCancel(sessionId);
    }

    #region Session 读取方法

    /// <inheritdoc/>
    public async Task<SessionData?> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        return _sessionManager.Get(sessionId);
    }

    /// <inheritdoc/>
    public async Task<SessionData> EnsureSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        return await _sessionManager.EnsureSessionAsync(sessionId);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SessionData>> ListSessionsAsync(CancellationToken cancellationToken = default)
    {
        // 从存储加载所有会话
        var sessions = new List<SessionData>();
        var asyncEnumerable = await _sessionStore.ListAsync();
        
        await foreach (var session in asyncEnumerable.WithCancellation(cancellationToken))
        {
            sessions.Add(session);
            // 注册到内存缓存
            _sessionManager.Register(session);
        }
        
        return sessions.OrderByDescending(s => s.UpdatedAt).ToList();
    }

    /// <inheritdoc/>
    public async Task<SessionData> CreateSessionAsync(string? title = null, string? agentId = null, string? workingDirectory = null, CancellationToken cancellationToken = default)
    {
        var session = _sessionManager.Create(selectedAgent: agentId);
        
        if (!string.IsNullOrEmpty(title))
        {
            session.Title = title;
        }
        
        if (!string.IsNullOrEmpty(workingDirectory))
        {
            session.WorkingDirectory = workingDirectory;
        }
        
        await _sessionManager.SaveAsync(session.Id);
        
        _logger.LogInformation("Created session: {SessionId}, Title: {Title}", session.Id, session.Title);
        return session;
    }

    /// <inheritdoc/>
    public async Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        _sessionManager.Delete(sessionId);
        _logger.LogInformation("Deleted session: {SessionId}", sessionId);
    }

    /// <inheritdoc/>
    public async Task RenameSessionAsync(string sessionId, string newTitle, CancellationToken cancellationToken = default)
    {
        var session = await _sessionManager.LoadAsync(sessionId);
        if (session != null)
        {
            session.Title = newTitle;
            await _sessionManager.SaveAsync(sessionId);
            _logger.LogInformation("Renamed session: {SessionId} -> {NewTitle}", sessionId, newTitle);
        }
    }

    /// <inheritdoc/>
    public async Task<SessionData> BranchSessionAsync(string sessionId, string? title = null, CancellationToken cancellationToken = default)
    {
        var sourceSession = await _sessionManager.LoadAsync(sessionId);
        if (sourceSession == null)
        {
            throw new InvalidOperationException($"Session '{sessionId}' not found");
        }
        
        // 创建新会话，复制源会话的内容
        var newSession = _sessionManager.Create(selectedAgent: sourceSession.SelectedAgent);
        newSession.Title = title ?? string.Format("{0} (分支)", sourceSession.Title);
        newSession.WorkingDirectory = sourceSession.WorkingDirectory;
        newSession.Messages = new List<SessionMessage>(sourceSession.Messages);
        
        await _sessionManager.SaveAsync(newSession.Id);
        
        _logger.LogInformation("Branched session: {SourceId} -> {NewId}", sessionId, newSession.Id);
        return newSession;
    }

    #endregion

    #region Private Methods

    /// <summary>
    /// 构建执行上下文
    /// </summary>
    private async Task<ChatExecutionContext> BuildExecutionContextAsync(
        string sessionId,
        ChatInput input,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        // 解析 Agent
        var agentId = options?.AgentId 
            ?? await _agentSelectionResolver.ResolveAgentIdAsync(null, null, cancellationToken);
        var agent = _agentRegistry.GetOrCreateAgentInstance(agentId);
        
        if (agent == null)
        {
            throw new InvalidOperationException($"Agent '{agentId}' not found");
        }
        
        var agentDef = AgentDefinition.FromAgent(agent);
        
        // 构建上下文
        return new ChatExecutionContext
        {
            SessionId = sessionId,
            Agent = agentDef,
            History = new List<ChatMessage>(),
            WorkingDirectory = options?.WorkingDirectory ?? _workspaceProvider.WorkspaceRoot,
            WorkspaceRoot = _workspaceProvider.WorkspaceRoot,
            PermissionChannel = _permissionChannel,
            ChannelId = options?.ChannelId,
            UserId = options?.UserId,
            AcpModeId = options?.ModeId,
            AcpModelId = options?.ModelId
        };
    }

    /// <summary>
    /// 构建用户消息
    /// </summary>
    private SessionMessage BuildUserMessage(ChatInput input)
    {
        var parts = new List<SessionContentPart>();
        
        if (!string.IsNullOrWhiteSpace(input.Text))
        {
            parts.Add(SessionContentPart.CreateText(input.Text));
        }
        
        if (input.Attachments != null && input.Attachments.Count > 0)
        {
            foreach (var att in input.Attachments)
            {
                if (att.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    parts.Add(SessionContentPart.CreateImageFromBase64(att.Base64Data, att.MimeType));
                }
                else
                {
                    parts.Add(SessionContentPart.CreateFileFromBase64(att.Base64Data, att.MimeType, att.FileName));
                }
            }
        }
        
        return parts.Count > 1 || (input.Attachments != null && input.Attachments.Count > 0)
            ? SessionMessage.UserMessageWithParts(parts)
            : SessionMessage.UserMessage(input.Text ?? "");
    }

    /// <summary>
    /// 从 Session 构建 History
    /// </summary>
    private List<ChatMessage> BuildHistoryFromSession(SessionData session)
    {
        var history = new List<ChatMessage>();
        
        foreach (var msg in session.Messages)
        {
            var chatMessage = new ChatMessage
            {
                Role = msg.Role,
                Content = msg.Content,
                ReasoningContent = msg.ReasoningContent
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
        }
        
        return history;
    }

    /// <summary>
    /// 处理命令
    /// </summary>
    private async IAsyncEnumerable<IMessageEvent?> ProcessCommandAsync(
        string sessionId,
        string input,
        SessionData session,
        ChatExecutionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var cmdName = ParseCommandName(input);
        var command = _commandRegistry.GetCommand(cmdName);
        
        _logger.LogInformation("ProcessCommandAsync: cmdName={CmdName}, command={Command}, allCommands={AllCommands}", 
            cmdName, 
            command?.Metadata.Name ?? "null",
            string.Join(", ", _commandRegistry.GetAllCommands().Select(c => c.Metadata.Name)));
        
        if (command == null)
        {
            // 未知命令，透传给 Agent
            _logger.LogDebug("Unknown command, passing through: {CommandName}", cmdName);
            yield return null;
            yield break;
        }
        
        var cmdType = command.Metadata.Type;
        var args = ExtractArguments(input, cmdName);
        
        var cmdContext = new CommandContext
        {
            CommandName = cmdName,
            RawInput = input,
            Arguments = args,
            SessionId = sessionId,
            WorkspaceRoot = context.WorkspaceRoot,
            History = context.History,
            CancellationToken = cancellationToken
        };
        
        var result = await _commandDispatcher.HandleAsync(input, cmdContext, cancellationToken);
        
        // 系统命令 → 返回结果
        if (cmdType == CommandType.System)
        {
            yield return new CommandResultEvent
            {
                SessionId = sessionId,
                CommandName = cmdName,
                Success = result.Success,
                Message = result.Success ? result.Message : result.ErrorMessage
            };
            
            if (result.GetNavigationTarget() is string target)
            {
                yield return new NavigateEvent
                {
                    SessionId = sessionId,
                    Target = target
                };
            }
            
            yield break;
        }
        
        // Skill 命令 → 展开 History
        if (cmdType == CommandType.Skill)
        {
            if (!result.Success)
            {
                yield return new CommandResultEvent
                {
                    SessionId = sessionId,
                    CommandName = cmdName,
                    Success = false,
                    Message = result.ErrorMessage ?? "Skill command failed"
                };
                yield break;
            }
            
            var expandedContent = result.GetExpandedContent();
            if (expandedContent != null)
            {
                // 更新 Session 中的用户消息
                if (session.Messages.Count > 0)
                {
                    session.Messages[^1].Content = expandedContent;
                }
                
                // 发送 Skill 内容展开事件
                yield return new SkillContentEvent
                {
                    SessionId = sessionId,
                    OriginalContent = result.GetOriginalContent() ?? input,
                    ExpandedContent = expandedContent
                };
            }
            
            // 继续执行 Agent
            yield return null;
        }
    }

    /// <summary>
    /// 构建 AgentContext
    /// </summary>
    private AgentContext BuildAgentContext(ChatExecutionContext context, CancellationToken cancellationToken)
    {
        return new AgentContext
        {
            SessionId = context.SessionId,
            History = context.History,
            WorkingDirectory = context.WorkingDirectory ?? context.WorkspaceRoot ?? "",
            WorkspaceRoot = context.WorkspaceRoot ?? "",
            PermissionChannel = context.PermissionChannel,
            CancellationToken = cancellationToken
        };
    }

    private static string ParseCommandName(string input)
    {
        var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[0].TrimStart('/') : "";
    }

    private static string ExtractArguments(string input, string cmdName)
    {
        var prefix = "/" + cmdName;
        var idx = input.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? input.Substring(idx + prefix.Length).Trim() : "";
    }

    #endregion
}
