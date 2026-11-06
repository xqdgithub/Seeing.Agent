namespace Seeing.Agent.Hosting.Execution.ToolExposure;

/// <summary>
/// 一次 <c>tools[]</c> 相对上一轮的变化。
/// </summary>
/// <param name="Changed">是否发生任何变化（含仅顺序变化——前缀缓存对顺序同样敏感）。</param>
/// <param name="Added">新增的工具 id（按本轮顺序）。</param>
/// <param name="Removed">消失的工具 id（按上一轮顺序）。</param>
public readonly record struct ToolSchemaDiff(
    bool Changed,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed)
{
    /// <summary>无变化。</summary>
    public static ToolSchemaDiff None { get; } = new(false, Array.Empty<string>(), Array.Empty<string>());
}

/// <summary>
/// 前缀缓存运行时守门：记录每个会话上一轮的 <c>tools[]</c>，在两轮之间比较并报告差异。
/// <para>
/// 存在理由：<c>tools[]</c> 一旦变化，厂商侧前缀缓存从变化点起全量失效。这是**性能事故**而非功能故障，
/// 没有用户可见报错，只能靠观测发现。本监控把「哪一轮开始变、变了什么」变成可检索的事实。
/// </para>
/// <para>
/// 已知的合法变化：Agent / 场景切换、MCP 连接增减、跨日（环境分节）。因此本类**只报告不阻断**，
/// 判定是否异常由调用方（日志级别、指标）决定。
/// </para>
/// <para>线程安全；按 SessionId 隔离。进程内状态，不持久化（重启后首轮视为无基线）。</para>
/// </summary>
public sealed class ToolSchemaStabilityMonitor
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string[]> _lastBySession = new(StringComparer.Ordinal);

    /// <summary>
    /// 登记本轮的 <c>tools[]</c> 并与上一轮比较。
    /// </summary>
    /// <param name="sessionId">会话 id。</param>
    /// <param name="toolIds">本轮内联进请求的工具 id（顺序即下发顺序）。</param>
    /// <returns>与上一轮的差异；首轮或会话 id 为空时返回 <see cref="ToolSchemaDiff.None"/>。</returns>
    public ToolSchemaDiff Track(string sessionId, IReadOnlyList<string> toolIds)
    {
        ArgumentNullException.ThrowIfNull(toolIds);

        if (string.IsNullOrWhiteSpace(sessionId))
            return ToolSchemaDiff.None;

        var current = toolIds.ToArray();

        lock (_gate)
        {
            if (!_lastBySession.TryGetValue(sessionId, out var previous))
            {
                _lastBySession[sessionId] = current;
                return ToolSchemaDiff.None;
            }

            // 顺序也算变化：缓存按前缀工作，元素位移等同于从该点起全量失效。
            if (previous.AsSpan().SequenceEqual(current))
                return ToolSchemaDiff.None;

            var previousSet = new HashSet<string>(previous, StringComparer.Ordinal);
            var currentSet = new HashSet<string>(current, StringComparer.Ordinal);

            var added = current.Where(id => !previousSet.Contains(id)).ToArray();
            var removed = previous.Where(id => !currentSet.Contains(id)).ToArray();

            _lastBySession[sessionId] = current;
            return new ToolSchemaDiff(true, added, removed);
        }
    }

    /// <summary>丢弃某会话的基线（会话清空 / 结束时调用，避免字典无限增长）。</summary>
    public void Forget(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        lock (_gate)
            _lastBySession.Remove(sessionId);
    }
}
