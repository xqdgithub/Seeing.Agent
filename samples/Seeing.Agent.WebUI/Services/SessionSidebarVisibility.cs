namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 会话侧栏可见性状态机（按组记忆）。
/// <para>
/// 规则（与产品约定一致）：
/// <list type="number">
///   <item>首次加载：自动显示。</item>
///   <item>用户手动隐藏后保持隐藏，组事件重放同一批成员不会重新弹出。</item>
///   <item>组内**新增成员**时自动展开。</item>
///   <item>组内已有成员**开始执行**时自动展开（复用已有子 Agent 也算）—— 执行结束不展开。</item>
///   <item>隐藏状态按组记忆：切换组再切回来仍保持隐藏；不同组互不影响。</item>
/// </list>
/// </para>
/// <para>
/// 纯内存、非线程安全：调用方保证在 Blazor Dispatcher 线程使用。
/// 不持久化（刷新后回到「自动显示」），避免用户某天发现侧栏莫名不出来。
/// </para>
/// </summary>
public sealed class SessionSidebarVisibility
{
    /// <summary>各组「已被用户隐藏」标记。</summary>
    private readonly Dictionary<string, bool> _hiddenByGroup = new(StringComparer.Ordinal);

    /// <summary>各组上次观察到的成员 Id 集合（用于判定「新增成员」）。</summary>
    private readonly Dictionary<string, HashSet<string>> _knownMembersByGroup = new(StringComparer.Ordinal);

    /// <summary>当前侧栏是否应显示。</summary>
    /// <param name="groupId">组 Id；空表示无组。</param>
    /// <param name="memberCount">组成员数（不含 active 的侧栏条目数）。为 0 时不显示。</param>
    public bool ShouldShow(string? groupId, int memberCount = 1)
    {
        if (string.IsNullOrEmpty(groupId) || memberCount <= 0)
            return false;

        return !_hiddenByGroup.TryGetValue(groupId, out var hidden) || !hidden;
    }

    /// <summary>观察一次组快照：记录成员基线，并在检测到新增成员时自动展开。</summary>
    public void Observe(string? groupId, IEnumerable<string> memberIds)
    {
        if (string.IsNullOrEmpty(groupId))
            return;

        var current = new HashSet<string>(memberIds ?? Array.Empty<string>(), StringComparer.Ordinal);

        if (_knownMembersByGroup.TryGetValue(groupId, out var known))
        {
            // 空基线不算「新增」：组刚建立时不该触发自动展开逻辑
            if (known.Count > 0 && current.Count > known.Count && !current.IsSubsetOf(known))
            {
                _hiddenByGroup[groupId] = false;
            }
        }

        _knownMembersByGroup[groupId] = current;
    }

    /// <summary>某个会话开始/结束执行：开始执行时自动展开（仅当该会话属于当前组）。</summary>
    public void NotifyExecuting(string? sessionId, bool isExecuting, string? groupId = null)
    {
        if (!isExecuting || string.IsNullOrEmpty(sessionId))
            return;

        foreach (var (group, members) in _knownMembersByGroup)
        {
            if (groupId is not null && !string.Equals(group, groupId, StringComparison.Ordinal))
                continue;

            if (members.Contains(sessionId))
            {
                _hiddenByGroup[group] = false;
                return;
            }
        }
    }

    /// <summary>用户手动隐藏。</summary>
    public void Hide(string? groupId)
    {
        if (string.IsNullOrEmpty(groupId))
            return;

        _hiddenByGroup[groupId] = true;
    }

    /// <summary>用户手动展开。</summary>
    public void Show(string? groupId)
    {
        if (string.IsNullOrEmpty(groupId))
            return;

        _hiddenByGroup[groupId] = false;
    }

    /// <summary>切换可见性。</summary>
    public void Toggle(string? groupId)
    {
        if (string.IsNullOrEmpty(groupId))
            return;

        // 直接按记录的隐藏态取反，不要绕 ShouldShow（后者还受 memberCount 影响）
        var currentlyHidden = _hiddenByGroup.TryGetValue(groupId, out var hidden) && hidden;
        _hiddenByGroup[groupId] = !currentlyHidden;
    }
}
