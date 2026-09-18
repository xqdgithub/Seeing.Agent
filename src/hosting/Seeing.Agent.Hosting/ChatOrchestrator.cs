using Seeing.Agent.Abstractions.Chat;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Configuration;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Models;
using Seeing.Agent.Hosting.Execution;
using Seeing.Agent.Core.Commands;
using Seeing.Agent.Core;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Core.Llm;
using Seeing.Agent.Llm;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Configuration;
using Seeing.Session.Core;

namespace Seeing.Agent.Hosting;

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
    private readonly ISessionGroupManager _groupManager;
    private readonly IAgentRegistry _agentRegistry;
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly IAgentExecutor _executionRouter;
    private readonly ICommandRegistry _commandRegistry;
    private readonly AgentSelectionResolver _agentSelectionResolver;
    private readonly IModelManager _modelManager;
    private readonly ChatExecutionQueue _executionQueue;
    private readonly ChatRunTracker _runTracker;
    private readonly IDefaultWorkModeProvider? _defaultWorkMode;
    private readonly ILogger<ChatOrchestrator> _logger;

    public ChatOrchestrator(
        ExecutionJobService executionJobService,
        ISessionManager sessionManager,
        ISessionGroupManager groupManager,
        IAgentRegistry agentRegistry,
        IWorkspaceProvider workspaceProvider,
        IAgentExecutor executionRouter,
        ICommandRegistry commandRegistry,
        AgentSelectionResolver agentSelectionResolver,
        IModelManager modelManager,
        ChatExecutionQueue executionQueue,
        ChatRunTracker runTracker,
        ILogger<ChatOrchestrator> logger,
        IDefaultWorkModeProvider? defaultWorkMode = null)
    {
        _executionJobService = executionJobService;
        _sessionManager = sessionManager;
        _groupManager = groupManager;
        _agentRegistry = agentRegistry;
        _workspaceProvider = workspaceProvider;
        _executionRouter = executionRouter;
        _commandRegistry = commandRegistry;
        _agentSelectionResolver = agentSelectionResolver;
        _modelManager = modelManager;
        _executionQueue = executionQueue;
        _runTracker = runTracker;
        _logger = logger;
        _defaultWorkMode = defaultWorkMode;
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
        return await _sessionManager.EnsureSessionAsync(
            sessionId,
            scenario: _defaultWorkMode?.GetDefaultScenario());
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SessionData>> ListSessionsAsync(CancellationToken cancellationToken = default)
    {
        // 锚点会话（每组单一 Root）：ListAnchorsAsync 内部已完成存储加载与组物化
        return await _groupManager.ListAnchorsAsync(ct: cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<SessionData> CreateSessionAsync(string? title = null, string? agentId = null, string? workingDirectory = null, CancellationToken cancellationToken = default)
    {
        var resolvedAgentId = await _agentSelectionResolver.ResolveAgentIdAsync(
            agentId,
            sessionSelectedAgent: null,
            cancellationToken).ConfigureAwait(false);

        var session = await _groupManager.CreateRootAsync(
            partitionId: null,
            agent: resolvedAgentId,
            scenario: _defaultWorkMode?.GetDefaultScenario(),
            title: string.IsNullOrEmpty(title) ? "新会话" : title,
            workingDirectory: workingDirectory,
            ct: cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        _modelManager.SeedSessionModel(session, resolvedAgentId);

        await _sessionManager.SaveAsync(session.Id);

        _logger.LogInformation(
            "Created session: {SessionId}, Title: {Title}, Agent: {Agent}, Model: {Model}, Scenario: {Scenario}",
            session.Id,
            session.Title,
            session.SelectedAgent,
            string.IsNullOrEmpty(session.SelectedModel)
                ? "(none)"
                : session.SelectedModel,
            session.Scenario ?? "(none)");
        return session;
    }

    /// <inheritdoc/>
    public async Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        // 先取消被删会话自身的在途执行，避免删除后执行仍向已移除会话写入
        try
        {
            await _executionJobService.CancelBySessionAsync(sessionId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "删除会话时取消自身执行失败: {SessionId}", sessionId);
        }

        // 再取消子会话在途执行，然后交给组管理器递归删除 Child 子树，避免孤儿数据与悬挂执行
        var children = await _groupManager.ListChildrenAsync(sessionId, cancellationToken);
        foreach (var child in children)
        {
            try
            {
                await _executionJobService.CancelBySessionAsync(child.Id, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "删除会话时取消子会话执行失败: {ChildSessionId}", child.Id);
            }
        }

        await _groupManager.RemoveSessionAsync(sessionId, cancellationToken);
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
        var sourceSession = _sessionManager.Get(sessionId) ?? await _sessionManager.LoadAsync(sessionId);
        if (sourceSession == null)
        {
            throw new InvalidOperationException($"Session '{sessionId}' not found");
        }

        // 分支关系（新组 / 加入源组 + Relation=Fork）由组管理器统一承载，消息深拷贝在 SessionForker 内完成
        var resolvedTitle = title ?? string.Format("{0} (分支)", sourceSession.Title);
        var newSession = await _groupManager.ForkSessionAsync(sessionId, resolvedTitle, cancellationToken);

        _logger.LogInformation("Branched session: {SourceId} -> {NewId}", sessionId, newSession.Id);
        return newSession;
    }

    #endregion
}
