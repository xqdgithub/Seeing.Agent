using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Session.Core;

namespace Seeing.Agent.Hosting.Execution.ToolExposure;

/// <summary>
/// 会话级发现态写入：把执行中途检索到的工具登记为「已发现」。
/// <para>
/// 默认（<c>PromoteOnSearch=false</c>）只用于观测「模型想加载什么」，不影响可调用性或 `tools[]`；
/// 开启 promote 时才作为「下一次执行起内联」的待生效集。
/// </para>
/// <para>不触发落盘：持久化由执行链的单一权威写入者统一保存。</para>
/// </summary>
public sealed class SessionToolDiscoveryStore : IToolDiscoveryStore
{
    private readonly ISessionManager _sessionManager;
    private readonly ILogger<SessionToolDiscoveryStore>? _logger;

    public SessionToolDiscoveryStore(
        ISessionManager sessionManager,
        ILogger<SessionToolDiscoveryStore>? logger = null)
    {
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _logger = logger;
    }

    /// <inheritdoc />
    public bool Add(string sessionId, IEnumerable<string> toolIds)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || toolIds is null)
            return false;

        var session = _sessionManager.Get(sessionId);
        if (session is null)
        {
            // 不静默成功：调用方据此告知模型写入失败，而不是承诺一个不会兑现的"下一轮可用"。
            _logger?.LogWarning("发现态写入时会话不在缓存，写入失败: SessionId={SessionId}", sessionId);
            return false;
        }

        var added = session.ToolExposure.AddPending(toolIds);
        if (added > 0)
        {
            _logger?.LogDebug("会话待生效发现追加: SessionId={SessionId}, Added={Added}, Pending={Pending}",
                sessionId, added, session.ToolExposure.GetPending().Count);
        }

        return true;
    }
}
