using System.Collections.Concurrent;
using Seeing.Agent.Abstractions.Permissions;

namespace Seeing.Agent.Core.Permission;

/// <summary>
/// 会话「可交互」计数：Attach/Detach 记账，CanPresent 判定是否存在可应答的呈现端。
/// </summary>
public sealed class PermissionPresenceStore : IPermissionPresenceStore
{
    private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public void Attach(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return;

        _counts.AddOrUpdate(sessionId, static _ => 1, static (_, count) => count + 1);
    }

    /// <inheritdoc />
    public void Detach(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return;

        while (_counts.TryGetValue(sessionId, out var current))
        {
            if (current <= 1)
            {
                if (_counts.TryRemove(new KeyValuePair<string, int>(sessionId, current)))
                    return;
            }
            else if (_counts.TryUpdate(sessionId, current - 1, current))
            {
                return;
            }
        }
    }

    /// <inheritdoc />
    public bool CanPresent(string sessionId) =>
        !string.IsNullOrEmpty(sessionId) &&
        _counts.TryGetValue(sessionId, out var count) &&
        count > 0;

    /// <inheritdoc />
    public void ClearAll() => _counts.Clear();
}
