using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Agent.Core;
using Seeing.Agent.Llm;
using Seeing.Session.Core;

namespace Seeing.Agent.Hosting;

/// <summary>
/// 会话目录 - 会话 CRUD 的唯一权威入口（实现 <see cref="ISessionDirectory"/>）。
/// <para>
/// 从 <see cref="ChatOrchestrator"/> 迁出的纯会话管理职责：读取、确保、列举、创建、删除、重命名、分支。
/// 不承载执行/事件编排。
/// </para>
/// </summary>
public sealed class SessionDirectory : ISessionDirectory
{
    private readonly ISessionManager _sessionManager;
    private readonly ISessionGroupManager _groupManager;
    private readonly AgentSelectionResolver _agentSelectionResolver;
    private readonly IModelManager _modelManager;
    private readonly IExecutionSubmitter _executionSubmitter;
    private readonly ILogger<SessionDirectory> _logger;
    private readonly IDefaultWorkModeProvider? _defaultWorkMode;

    public SessionDirectory(
        ISessionManager sessionManager,
        ISessionGroupManager groupManager,
        AgentSelectionResolver agentSelectionResolver,
        IModelManager modelManager,
        IExecutionSubmitter executionSubmitter,
        ILogger<SessionDirectory> logger,
        IDefaultWorkModeProvider? defaultWorkMode = null)
    {
        _sessionManager = sessionManager;
        _groupManager = groupManager;
        _agentSelectionResolver = agentSelectionResolver;
        _modelManager = modelManager;
        _executionSubmitter = executionSubmitter;
        _logger = logger;
        _defaultWorkMode = defaultWorkMode;
    }

    /// <inheritdoc/>
    public async Task<SessionData?> GetAsync(string sessionId, CancellationToken ct = default)
    {
        // 优先内存缓存（执行中的 Task 字段尚未落盘时不能被磁盘旧快照覆盖）
        var cached = _sessionManager.Get(sessionId);
        if (cached != null)
            return cached;

        return await _sessionManager.LoadAsync(sessionId);
    }

    /// <inheritdoc/>
    public async Task<SessionData> EnsureAsync(string sessionId, CancellationToken ct = default)
    {
        return await _sessionManager.EnsureSessionAsync(
            sessionId,
            scenario: _defaultWorkMode?.GetDefaultScenario());
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SessionData>> ListAsync(CancellationToken ct = default)
    {
        // 锚点会话（每组单一 Root）：ListAnchorsAsync 内部已完成存储加载与组物化
        return await _groupManager.ListAnchorsAsync(ct: ct);
    }

    /// <inheritdoc/>
    public async Task<SessionData> CreateAsync(
        string? title = null,
        string? agentId = null,
        string? workingDirectory = null,
        CancellationToken ct = default)
    {
        var resolvedAgentId = await _agentSelectionResolver.ResolveAgentIdAsync(
            agentId,
            sessionSelectedAgent: null,
            ct).ConfigureAwait(false);

        var session = await _groupManager.CreateRootAsync(
            partitionId: null,
            agent: resolvedAgentId,
            scenario: _defaultWorkMode?.GetDefaultScenario(),
            title: string.IsNullOrEmpty(title) ? "新会话" : title,
            workingDirectory: workingDirectory,
            ct: ct).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();
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
    public async Task DeleteAsync(string sessionId, CancellationToken ct = default)
    {
        // 先取消被删会话自身的在途执行，避免删除后执行仍向已移除会话写入
        try
        {
            await _executionSubmitter.CancelBySessionAsync(sessionId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "删除会话时取消自身执行失败: {SessionId}", sessionId);
        }

        // 再取消子会话在途执行，然后交给组管理器递归删除 Child 子树，避免孤儿数据与悬挂执行
        var children = await _groupManager.ListChildrenAsync(sessionId, ct);
        foreach (var child in children)
        {
            try
            {
                await _executionSubmitter.CancelBySessionAsync(child.Id, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "删除会话时取消子会话执行失败: {ChildSessionId}", child.Id);
            }
        }

        await _groupManager.RemoveSessionAsync(sessionId, ct);
        _logger.LogInformation("Deleted session: {SessionId}", sessionId);
    }

    /// <inheritdoc/>
    public async Task RenameAsync(string sessionId, string newTitle, CancellationToken ct = default)
    {
        var session = _sessionManager.Get(sessionId) ?? await _sessionManager.LoadAsync(sessionId);
        if (session == null)
            return;

        // 统一走 SetTitleAsync，保证 SessionEvent / 持久化路径一致
        await _sessionManager.SetTitleAsync(sessionId, newTitle, ct);
        _logger.LogInformation("Renamed session: {SessionId} -> {NewTitle}", sessionId, newTitle);
    }

    /// <inheritdoc/>
    public async Task<SessionData> BranchAsync(string sessionId, string? title = null, CancellationToken ct = default)
    {
        var sourceSession = _sessionManager.Get(sessionId) ?? await _sessionManager.LoadAsync(sessionId);
        if (sourceSession == null)
        {
            throw new InvalidOperationException($"Session '{sessionId}' not found");
        }

        // 分支关系（新组 / 加入源组 + Relation=Fork）由组管理器统一承载，消息深拷贝在 SessionForker 内完成
        var resolvedTitle = title ?? string.Format("{0} (分支)", sourceSession.Title);
        var newSession = await _groupManager.ForkSessionAsync(sessionId, resolvedTitle, ct);

        _logger.LogInformation("Branched session: {SourceId} -> {NewId}", sessionId, newSession.Id);
        return newSession;
    }
}
