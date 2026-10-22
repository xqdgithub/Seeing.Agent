using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Session.Core;

namespace Seeing.Agent.Hosting.Execution.ToolExposure;

/// <summary>
/// 会话级工具发现记录存取：直接读写 <see cref="SessionData.DiscoveredTools"/> 内存对象。
/// <para>落盘策略：本类只更新内存、不触发 SaveAsync——持久化由执行链的单一权威写入者 save 负责，
/// 避免双写竞态与额外落盘成本。</para>
/// <para>单调语义：Add 去重追加（无上限淘汰）；Prune 仅在工具消失时裁剪，保持剩余发现顺序；
/// ClearMessages 不清除发现态（见 <see cref="SessionData.DiscoveredTools"/>）。</para>
/// </summary>
public sealed class SessionToolDiscoveryStore : IToolDiscoveryStore
{
    private readonly ISessionManager _sessionManager;
    private readonly ILogger<SessionToolDiscoveryStore>? _logger;

    public SessionToolDiscoveryStore(
        ISessionManager sessionManager,
        ILogger<SessionToolDiscoveryStore>? logger = null)
    {
        _sessionManager = sessionManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlySet<string> Get(string sessionId)
    {
        var session = _sessionManager.Get(sessionId);
        if (session is null)
        {
            _logger?.LogDebug("发现态读取时会话不在缓存，返回空集: SessionId={SessionId}", sessionId);
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return new HashSet<string>(session.GetDiscoveredToolsSnapshot(), StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public void Add(string sessionId, IEnumerable<string> toolIds)
    {
        if (toolIds is null)
            return;

        var session = _sessionManager.Get(sessionId);
        if (session is null)
        {
            _logger?.LogDebug("发现态追加时会话不在缓存，跳过: SessionId={SessionId}", sessionId);
            return;
        }

        // 加锁去重追加（同一锁序列与 resolver 的替换/快照互斥）。
        var before = session.GetDiscoveredToolsSnapshot().Count;
        session.AppendDiscoveredTools(toolIds);

        if (session.GetDiscoveredToolsSnapshot().Count != before)
            _logger?.LogDebug("会话发现态追加: SessionId={SessionId}, Total={Count}",
                sessionId, session.GetDiscoveredToolsSnapshot().Count);
    }

    /// <inheritdoc />
    public void Prune(string sessionId, IReadOnlySet<string> availableToolIds)
    {
        if (availableToolIds is null)
            return;

        var session = _sessionManager.Get(sessionId);
        if (session is null)
        {
            _logger?.LogDebug("发现态裁剪时会话不在缓存，跳过: SessionId={SessionId}", sessionId);
            return;
        }

        // 原子裁剪（锁内读-过滤-替换），避免与并发 Append 的 TOCTOU 丢更新。
        session.PruneDiscoveredTools(availableToolIds);
    }
}
