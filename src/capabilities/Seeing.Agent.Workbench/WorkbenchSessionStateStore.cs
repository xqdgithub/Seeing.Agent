using Seeing.Agent.Abstractions.Ui.Workbench;

namespace Seeing.Agent.Workbench;

/// <summary>
/// 会话工作台快照存储（Singleton）：UI 写、服务端读。
/// <para>
/// 并发契约：写者（各 circuit 的 <c>WorkbenchHostBridge</c>）在锁外构造不可变列表后整批替换；
/// 读者拿到的永远是某个完整快照，不会看到半更新的集合。
/// 同会话多 circuit 时<b>后写覆盖</b>——与权限 <c>TryAutoApprove</c> 的进程级语义属同类已知边界。
/// </para>
/// </summary>
public sealed class WorkbenchSessionStateStore : IWorkbenchSessionStateStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, IReadOnlyList<WorkbenchSessionSnapshot>> _bySession =
        new(StringComparer.Ordinal);

    /// <inheritdoc />
    public event Action<string>? Changed;

    /// <inheritdoc />
    public void Update(string sessionId, IReadOnlyList<WorkbenchSessionSnapshot> snapshots)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        ArgumentNullException.ThrowIfNull(snapshots);

        // 先判等再写：相同快照不触发 Changed，避免 UI 去抖后仍造成上下文源无谓重建
        lock (_gate)
        {
            var next = snapshots.ToArray();
            if (_bySession.TryGetValue(sessionId, out var existing) && SameSnapshot(existing, next))
                return;

            if (next.Length == 0)
                _bySession.Remove(sessionId);
            else
                _bySession[sessionId] = next;
        }

        RaiseChanged(sessionId);
    }

    /// <inheritdoc />
    public void Clear(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        bool removed;
        lock (_gate)
            removed = _bySession.Remove(sessionId);

        if (removed)
            RaiseChanged(sessionId);
    }

    /// <inheritdoc />
    public IReadOnlyList<WorkbenchSessionSnapshot> GetSnapshot(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return [];

        lock (_gate)
            return _bySession.TryGetValue(sessionId, out var snapshots) ? snapshots : [];
    }

    private static bool SameSnapshot(
        IReadOnlyList<WorkbenchSessionSnapshot> left,
        IReadOnlyList<WorkbenchSessionSnapshot> right)
    {
        if (left.Count != right.Count)
            return false;

        for (var i = 0; i < left.Count; i++)
        {
            var a = left[i];
            var b = right[i];
            if (!string.Equals(a.Key.ToString(), b.Key.ToString(), StringComparison.Ordinal)
                || !string.Equals(a.Title, b.Title, StringComparison.Ordinal)
                || a.Active != b.Active
                || !string.Equals(a.ContextSummary, b.ContextSummary, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private void RaiseChanged(string sessionId)
    {
        var handlers = Changed;
        if (handlers is null)
            return;

        // 订阅者异常必须隔离：一个源的失败不能让其它订阅者收不到通知
        foreach (var handler in handlers.GetInvocationList().Cast<Action<string>>())
        {
            try { handler(sessionId); }
            catch { /* 订阅者自负责；此处不得抛回写者（写者是 UI 渲染线程） */ }
        }
    }
}
