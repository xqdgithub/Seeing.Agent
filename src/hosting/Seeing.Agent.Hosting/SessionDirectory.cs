using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Agent.Core;
using Seeing.Agent.Llm;
using Seeing.Session.Core;

namespace Seeing.Agent.Hosting;

/// <summary>
/// 会话目录 - 会话 CRUD 的唯一权威入口（实现 <see cref="ISessionDirectory"/>）。
/// <para>
/// 从旧会话编排器迁出的纯会话管理职责：读取、确保、列举、创建、删除、重命名、分支。
/// 不承载执行/事件编排。
/// </para>
/// </summary>
public sealed class SessionDirectory : ISessionDirectory
{
    private readonly ISessionManager _sessionManager;
    private readonly ISessionGroupManager _groupManager;
    private readonly AgentSelectionResolver _agentSelectionResolver;
    private readonly IModelManager _modelManager;
    private readonly ISessionRuntime _runtime;
    private readonly ILogger<SessionDirectory> _logger;
    private readonly IDefaultWorkModeProvider? _defaultWorkMode;

    public SessionDirectory(
        ISessionManager sessionManager,
        ISessionGroupManager groupManager,
        AgentSelectionResolver agentSelectionResolver,
        IModelManager modelManager,
        ISessionRuntime runtime,
        ILogger<SessionDirectory> logger,
        IDefaultWorkModeProvider? defaultWorkMode = null)
    {
        _sessionManager = sessionManager;
        _groupManager = groupManager;
        _agentSelectionResolver = agentSelectionResolver;
        _modelManager = modelManager;
        _runtime = runtime;
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
        // 先中断在途消费循环（含级联子会话），避免删除后仍照常跑一轮写回已删会话。
        await _runtime.StopAsync(sessionId, ct).ConfigureAwait(false);

        // 交给组管理器递归删除 Child 子树。
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

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SessionSummary>> ListSummariesAsync(CancellationToken ct = default)
    {
        return await _groupManager.ListAnchorSummariesAsync(ct: ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SessionSummary>> ListArchivedAsync(CancellationToken ct = default)
    {
        var list = new List<SessionSummary>();
        await foreach (var summary in _sessionManager.ListArchivedAsync(ct).WithCancellation(ct).ConfigureAwait(false))
            list.Add(summary);

        return list.OrderByDescending(s => s.LastActiveAt).ToList();
    }

    /// <inheritdoc/>
    public async Task ArchiveAsync(string sessionId, CancellationToken ct = default)
    {
        // 归档前中断在途消费循环，避免归档后仍照常写回
        await _runtime.StopAsync(sessionId, ct).ConfigureAwait(false);

        var group = await _groupManager.GetGroupForSessionAsync(sessionId, ct).ConfigureAwait(false);
        if (group != null)
        {
            await _groupManager.ArchiveGroupAsync(group.Id, ct).ConfigureAwait(false);
        }
        else
        {
            await _sessionManager.ArchiveAsync(sessionId, ct).ConfigureAwait(false);
        }

        _logger.LogInformation("Archived session: {SessionId}", sessionId);
    }

    /// <inheritdoc/>
    public async Task RestoreAsync(string sessionId, CancellationToken ct = default)
    {
        var group = await _groupManager.GetGroupForSessionAsync(sessionId, ct).ConfigureAwait(false);
        if (group != null)
        {
            await _groupManager.RestoreGroupAsync(group.Id, ct).ConfigureAwait(false);
        }
        else
        {
            await _sessionManager.RestoreAsync(sessionId, ct).ConfigureAwait(false);
        }

        _logger.LogInformation("Restored session: {SessionId}", sessionId);
    }

    /// <inheritdoc/>
    public async Task EvictAsync(string sessionId, CancellationToken ct = default)
    {
        await _sessionManager.EvictAsync(sessionId, ct).ConfigureAwait(false);
        _logger.LogDebug("Evicted session from memory: {SessionId}", sessionId);
    }
}
