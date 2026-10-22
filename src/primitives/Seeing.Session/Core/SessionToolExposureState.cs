namespace Seeing.Session.Core;

/// <summary>
/// 会话级工具暴露状态（原语层，不含任何暴露策略语义，只提供加锁的状态存取）。
/// <para>
/// 两段式发现：<see cref="Pending"/> 接收执行中途的新发现，<see cref="Discovered"/> 是本轮已生效集合。
/// 仅在下一轮开头由 <see cref="PromotePending"/> 提升，因此同一轮内任何次数的结算输入都不变，
/// “发现下一轮生效”由结构保证而不是靠调用约定。
/// </para>
/// <para>
/// 并发契约与 <c>SessionData.Messages</c> 一致：所有写入在锁内**整体替换新列表**，绝不就地修改已发布实例，
/// 因此落盘序列化枚举到的列表恒定不变。公开属性及其 setter 仅供序列化。
/// </para>
/// </summary>
public sealed class SessionToolExposureState
{
    private readonly object _gate = new();

    /// <summary>本轮已生效的发现集（按发现顺序）。仅供序列化，读请走 <see cref="GetDiscovered"/>。</summary>
    public List<string> Discovered { get; set; } = new();

    /// <summary>执行中途新发现、尚未生效的工具（下一轮开头提升）。仅供序列化，读请走 <see cref="GetPending"/>。</summary>
    public List<string> Pending { get; set; } = new();

    /// <summary>升级粘性位（单调：一旦为真不再回退）。</summary>
    public bool Escalated { get; set; }

    /// <summary>最近一次结算的 inline 工具定义估算 token（每轮度量，随清空用量缓存而清）。</summary>
    public int? SchemaTokens { get; set; }

    /// <summary>最近一次结算的目录正文估算 token（每轮度量，随清空用量缓存而清）。</summary>
    public int? CatalogTokens { get; set; }

    /// <summary>已生效发现集快照。</summary>
    public IReadOnlyList<string> GetDiscovered()
    {
        lock (_gate)
            return (Discovered ?? new List<string>()).ToArray();
    }

    /// <summary>待生效发现集快照。</summary>
    public IReadOnlyList<string> GetPending()
    {
        lock (_gate)
            return (Pending ?? new List<string>()).ToArray();
    }

    /// <summary>
    /// 追加待生效发现（去重，已在生效集或待生效集中的忽略）。
    /// </summary>
    /// <returns>实际新增的数量。</returns>
    public int AddPending(IEnumerable<string> toolIds)
    {
        ArgumentNullException.ThrowIfNull(toolIds);
        lock (_gate)
        {
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in Discovered ?? new List<string>()) known.Add(id);
            var merged = new List<string>(Pending ?? new List<string>());
            foreach (var id in merged) known.Add(id);

            var added = 0;
            foreach (var id in toolIds)
            {
                if (!string.IsNullOrWhiteSpace(id) && known.Add(id))
                {
                    merged.Add(id);
                    added++;
                }
            }

            if (added > 0)
                Pending = merged;
            return added;
        }
    }

    /// <summary>
    /// 把待生效发现提升为已生效（保持顺序：既有生效项在前，新提升项在后；去重）。
    /// 每轮开头调用一次；无待生效项时不改动。
    /// </summary>
    public void PromotePending()
    {
        lock (_gate)
        {
            var pending = Pending ?? new List<string>();
            if (pending.Count == 0)
                return;

            var merged = new List<string>(Discovered ?? new List<string>());
            var known = new HashSet<string>(merged, StringComparer.OrdinalIgnoreCase);
            foreach (var id in pending)
            {
                if (known.Add(id))
                    merged.Add(id);
            }

            Discovered = merged;
            Pending = new List<string>();
        }
    }

    /// <summary>
    /// 提交一次暴露结算结果（每轮开头一次，唯一写入点）。
    /// <para>
    /// 发现集由结算引擎给出**裁剪后的结果**（只保留会话仍可用的工具），此处只持久化，
    /// 不重复实现裁剪谓词——裁剪语义只存在于引擎一处。
    /// </para>
    /// 度量与升级位直接覆盖；<paramref name="escalated"/> 为 true 即置位（单调粘滞）。
    /// </summary>
    public void Commit(
        IReadOnlyList<string>? discovered,
        bool escalated,
        int schemaTokens,
        int catalogTokens)
    {
        lock (_gate)
        {
            if (discovered is not null)
                Discovered = new List<string>(discovered);

            Escalated = Escalated || escalated;
            SchemaTokens = schemaTokens;
            CatalogTokens = catalogTokens;
        }
    }

    /// <summary>清空每轮度量（发现集、待生效集与升级位不受影响）。</summary>
    public void ClearMetrics()
    {
        lock (_gate)
        {
            SchemaTokens = null;
            CatalogTokens = null;
        }
    }

    /// <summary>
    /// 深拷贝。<paramref name="includeMetrics"/> 为 false 时不带每轮度量（分叉会话不继承上一会话的度量）。
    /// </summary>
    public SessionToolExposureState Clone(bool includeMetrics)
    {
        lock (_gate)
        {
            return new SessionToolExposureState
            {
                Discovered = new List<string>(Discovered ?? new List<string>()),
                Pending = new List<string>(Pending ?? new List<string>()),
                Escalated = Escalated,
                SchemaTokens = includeMetrics ? SchemaTokens : null,
                CatalogTokens = includeMetrics ? CatalogTokens : null
            };
        }
    }
}
