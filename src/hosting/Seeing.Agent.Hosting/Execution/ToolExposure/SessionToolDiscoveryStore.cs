using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Session.Core;

namespace Seeing.Agent.Hosting.Execution.ToolExposure;

/// <summary>
/// 发现态写入实现：把执行中途检索到的工具登记为"下一次执行起内联"。
/// <para>
/// 只写会话待生效集，不读不裁剪：读取由结算视图提供，裁剪由执行入口在轮首提交时完成。
/// 本次执行内的放行由执行器的可见性作用域负责，与本端口无关。
/// 因此不影响当前轮结算视图，天然满足"下一次执行起内联"。
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
