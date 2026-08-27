using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Abstractions.Agents;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Seeing.Agent.App.Events;
using Seeing.Agent.Execution;
using Seeing.Agent.Models;
using Seeing.Agent.Commands;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Llm;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Session.Core;

namespace Seeing.Agent.App;

/// <summary>
/// 聊天编排器 - 统一的 Agent 执行入口
/// <para>
/// 提供最小化的入口参数，内部管理 Session 生命周期、命令预处理、Agent 执行、事件发送。
/// </para>
/// </summary>
public class ChatOrchestrator : IChatOrchestrator
{
    private readonly ExecutionJobService _executionJobService;
    private readonly ISessionManager _sessionManager;
    private readonly IAgentRegistry _agentRegistry;
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly IAgentExecutor _executionRouter;
    private readonly ICommandRegistry _commandRegistry;
    private readonly AgentSelectionResolver _agentSelectionResolver;
    private readonly IModelManager _modelManager;
    private readonly ChatExecutionQueue _executionQueue;
    private readonly ChatRunTracker _runTracker;
    private readonly ILogger<ChatOrchestrator> _logger;

    public ChatOrchestrator(
        ExecutionJobService executionJobService,
        ISessionManager sessionManager,
        IAgentRegistry agentRegistry,
        IWorkspaceProvider workspaceProvider,
        IAgentExecutor executionRouter,
        ICommandRegistry commandRegistry,
        AgentSelectionResolver agentSelectionResolver,
        IModelManager modelManager,
        ChatExecutionQueue executionQueue,
        ChatRunTracker runTracker,
        ILogger<ChatOrchestrator> logger)
    {
        _executionJobService = executionJobService;
        _sessionManager = sessionManager;
        _agentRegistry = agentRegistry;
        _workspaceProvider = workspaceProvider;
        _executionRouter = executionRouter;
        _commandRegistry = commandRegistry;
        _agentSelectionResolver = agentSelectionResolver;
        _modelManager = modelManager;
        _executionQueue = executionQueue;
        _runTracker = runTracker;
        _logger = logger;
    }

    #region 新接口实现

    /// <inheritdoc/>
    public Task<ExecutionSubmitResult> SubmitAsync(string sessionId, ChatInput input, ChatOptions? options = null)
    {
        return _executionJobService.SubmitAsync(sessionId, input, options);
    }

    /// <inheritdoc/>
    public bool Cancel(string executionId)
    {
        return _executionJobService.Cancel(executionId);
    }

    /// <inheritdoc/>
    public Task<int> CancelBySessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        return _executionJobService.CancelBySessionAsync(sessionId, cancellationToken);
    }

    /// <inheritdoc/>
    public SessionExecutionOverview GetOverview(string sessionId)
    {
        return _executionJobService.GetOverview(sessionId);
    }

    /// <inheritdoc/>
    public ExecutionRecord? GetExecution(string executionId)
    {
        return _executionJobService.GetExecution(executionId);
    }

    /// <inheritdoc/>
    public IReadOnlyList<IMessageEvent> GetBufferedEvents(string sessionId)
    {
        return _executionJobService.GetBufferedEvents(sessionId);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<IMessageEvent> SubscribeEvents(string sessionId, CancellationToken cancellationToken = default)
    {
        return _executionJobService.SubscribeEvents(sessionId, cancellationToken);
    }

    #endregion

    #region Session 读取方法

    /// <inheritdoc/>
    public async Task<SessionData?> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        // 优先内存缓存（执行中的 Task 字段尚未落盘时不能被磁盘旧快照覆盖）
        var cached = _sessionManager.Get(sessionId);
        if (cached != null)
            return cached;

        return await _sessionManager.LoadAsync(sessionId);
    }

    /// <inheritdoc/>
    public async Task<SessionData> EnsureSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        return await _sessionManager.EnsureSessionAsync(sessionId);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SessionData>> ListSessionsAsync(CancellationToken cancellationToken = default)
    {
        // 装入磁盘会话后仅返回 Root（排除 Fork / SubAgent）
        await _sessionManager.LoadAllFromStorageAsync(cancellationToken);
        return await _sessionManager.ListRootsAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<SessionData> CreateSessionAsync(string? title = null, string? agentId = null, string? workingDirectory = null, CancellationToken cancellationToken = default)
    {
        var resolvedAgentId = await _agentSelectionResolver.ResolveAgentIdAsync(
            agentId,
            sessionSelectedAgent: null,
            cancellationToken).ConfigureAwait(false);

        var session = _sessionManager.Create(selectedAgent: resolvedAgentId);

        if (!string.IsNullOrEmpty(title))
        {
            session.Title = title;
        }
        else
        {
            session.Title = "新会话";
        }

        if (!string.IsNullOrEmpty(workingDirectory))
        {
            session.WorkingDirectory = workingDirectory;
        }

        cancellationToken.ThrowIfCancellationRequested();
        _modelManager.SeedSessionModel(session, resolvedAgentId);

        await _sessionManager.SaveAsync(session.Id);

        _logger.LogInformation(
            "Created session: {SessionId}, Title: {Title}, Agent: {Agent}, Model: {Model}",
            session.Id,
            session.Title,
            session.SelectedAgent,
            string.IsNullOrEmpty(session.SelectedModel)
                ? "(none)"
                : session.SelectedModel);
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
        var session = _sessionManager.Get(sessionId) ?? await _sessionManager.LoadAsync(sessionId);
        if (session == null)
            return;

        // 统一走 SetTitleAsync，保证 SessionEvent / 持久化路径一致
        await _sessionManager.SetTitleAsync(sessionId, newTitle, cancellationToken);
        _logger.LogInformation("Renamed session: {SessionId} -> {NewTitle}", sessionId, newTitle);
    }

    /// <inheritdoc/>
    public async Task<SessionData> BranchSessionAsync(string sessionId, string? title = null, CancellationToken cancellationToken = default)
    {
        var sourceSession = await _sessionManager.LoadAsync(sessionId);
        if (sourceSession == null)
        {
            throw new InvalidOperationException($"Session '{sessionId}' not found");
        }
        
        // 创建独立 Root 会话（无 Parent），可正常操作；从 SubAgent 分叉时脱离只读
        var newSession = _sessionManager.Create(selectedAgent: sourceSession.SelectedAgent);
        newSession.Kind = SessionKind.Root;
        newSession.ParentSessionId = null;
        newSession.ForkLabel = null;
        newSession.Title = title ?? string.Format("{0} (分支)", sourceSession.Title);
        newSession.WorkingDirectory = sourceSession.WorkingDirectory;
        newSession.SelectedModel = sourceSession.SelectedModel;
        // 深拷贝消息（元素共享会导致分支后原会话压缩标记污染分支会话的活跃消息）
        newSession.ReplaceMessages(sourceSession.Messages.Select(m => m.Clone()));
        if (sourceSession.Metadata.TryGetValue(SessionMetadataKeys.InstructionFingerprints, out var fingerprints)
            && !string.IsNullOrEmpty(fingerprints))
        {
            newSession.Metadata[SessionMetadataKeys.InstructionFingerprints] = fingerprints;
        }

        await _sessionManager.SaveAsync(newSession.Id);
        
        _logger.LogInformation("Branched session: {SourceId} -> {NewId} (Root, no parent)", sessionId, newSession.Id);
        return newSession;
    }

    #endregion
}
