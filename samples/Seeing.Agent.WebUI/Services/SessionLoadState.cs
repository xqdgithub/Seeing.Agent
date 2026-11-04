namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 会话装载状态机。
/// <para>
/// <b>为什么要独立成类</b>：切换会话涉及「目标会话、已装载会话、是否正在加载、是否显示骨架」
/// 四个量。用散落的布尔字段表达时，它们容易互相矛盾（例如骨架开着但加载已结束、
/// 或加载中却显示旧内容），只能靠不断加标志修补。这里把判定收敛成一条不变式：
/// </para>
/// <para>
/// <c>ShowSkeleton == IsLoading &amp;&amp; LoadedSessionId 非空</c>
/// </para>
/// <para>
/// 即：只有在「正在加载」且「已经有旧内容」时才需要遮挡。其余组合都不成立，
/// 因此不需要额外的开关来纠正。
/// </para>
/// <para>非线程安全：调用方保证在 Blazor Dispatcher 线程使用。</para>
/// </summary>
public sealed class SessionLoadState
{
    /// <summary>当前正在装载的目标会话 Id（null 表示空闲）。</summary>
    private string? _targetSessionId;

    /// <summary>最近一次成功装载的会话 Id。</summary>
    public string? LoadedSessionId { get; private set; }

    /// <summary>是否正在装载。</summary>
    public bool IsLoading => _targetSessionId is not null;

    /// <summary>
    /// 是否应显示骨架：正在装载 <b>且</b> 已有旧内容。
    /// <para>首次进入时 <see cref="LoadedSessionId"/> 为空，不会显示骨架（避免空闪）。</para>
    /// </summary>
    public bool ShowSkeleton => IsLoading && !string.IsNullOrEmpty(LoadedSessionId);

    /// <summary>
    /// 请求装载指定会话。
    /// <para>目标与已装载一致时视为幂等（重复导航 / 参数重放），不进入加载态。</para>
    /// </summary>
    /// <returns>是否需要真正执行装载。</returns>
    public bool Begin(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return false;

        if (string.Equals(sessionId, LoadedSessionId, StringComparison.Ordinal))
        {
            // 已是当前会话：不重复装载。若此前有未完成的目标（快速连点），也一并收敛。
            _targetSessionId = null;
            return false;
        }

        _targetSessionId = sessionId;
        return true;
    }

    /// <summary>装载成功。仅当完成的正是当前目标时才生效（丢弃迟到的过期完成）。</summary>
    public bool Complete(string? sessionId)
    {
        if (!string.Equals(sessionId, _targetSessionId, StringComparison.Ordinal))
            return false;

        LoadedSessionId = sessionId;
        _targetSessionId = null;
        return true;
    }

    /// <summary>装载失败：退出加载态，但不记录为已装载（下次参数变更会重试）。</summary>
    public void Fail(string? sessionId)
    {
        if (!string.Equals(sessionId, _targetSessionId, StringComparison.Ordinal))
            return;

        _targetSessionId = null;
    }
}
