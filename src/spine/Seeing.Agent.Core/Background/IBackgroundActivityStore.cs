using Seeing.Agent.Abstractions.Background;

namespace Seeing.Agent.Core.Background;

/// <summary>
/// 活动快照落盘端口（internal：只由 Core 内部与测试替身实现）。
/// <para>
/// <c>Running</c> 时写最小记录、终态时原子覆盖为完整记录；<c>Pending</c> 不落盘（spec §11.2）。
/// </para>
/// </summary>
internal interface IBackgroundActivityStore
{
    /// <summary>原子覆盖写一个活动快照。</summary>
    Task SaveAsync(BackgroundActivitySnapshot snapshot);

    /// <summary>读取单个活动快照；不存在返回 <see langword="null"/>。</summary>
    Task<BackgroundActivitySnapshot?> LoadAsync(string activityId);

    /// <summary>
    /// 批量读取活动快照（outbox 投递前一次性取齐本批次所需的展示信息，避免逐条调用）。
    /// 不存在的 id 直接跳过；空集合短路返回。
    /// <para>
    /// 必须区分「文件不存在」与「文件存在但读不出」：前者是正常清理，后者是瞬时 IO/JSON 失败，
    /// 调用方（outbox）据此决定是标 <c>activity_missing</c> 还是本轮重试。
    /// </para>
    /// </summary>
    Task<BackgroundActivitySnapshotBatch> LoadManyAsync(IReadOnlyCollection<string> activityIds);

    /// <summary>读取全部活动快照（孤儿扫描用）。</summary>
    Task<IReadOnlyList<BackgroundActivitySnapshot>> LoadAllAsync();

    /// <summary>
    /// 条件覆盖：在路径锁内重读磁盘记录，仅当它仍处于
    /// <paramref name="expectedStatus"/> 且 <see cref="BackgroundActivitySnapshot.HostInstanceId"/>
    /// 未变时，才用 <paramref name="replacement"/>（入参是刚重读到的最新快照）原子覆盖。
    /// <para>
    /// 孤儿收敛「读 → 判 → 覆盖」必须整段互斥，否则别的实例刚提交的终态会被旧快照盖回去。
    /// </para>
    /// </summary>
    /// <returns>实际写入返回 <see langword="true"/>；条件不满足返回 <see langword="false"/>。</returns>
    Task<bool> ReplaceIfUnchangedAsync(
        string activityId,
        BackgroundActivityStatus expectedStatus,
        string expectedHostInstanceId,
        Func<BackgroundActivitySnapshot, BackgroundActivitySnapshot> replacement);

    /// <summary>
    /// 删除该活动的落盘快照（保留期回收用）。记录不存在视为成功。
    /// <para>删除与写入共用同一条路径锁，避免与在途落盘交错。</para>
    /// </summary>
    Task DeleteAsync(string activityId);
}

/// <summary>
/// <see cref="IBackgroundActivityStore.LoadManyAsync"/> 的结果：
/// 找到的快照 + <b>存在但本次读不出</b>的活动 id（瞬时错误，可重试）。
/// <para>
/// 文件不存在的 id 既不在快照里、也不在 <see cref="UnreadableActivityIds"/> 里——
/// 那是正常清理，与「读失败」是两种语义。
/// </para>
/// </summary>
internal sealed record BackgroundActivitySnapshotBatch(
    IReadOnlyList<BackgroundActivitySnapshot> Snapshots,
    IReadOnlyList<string> UnreadableActivityIds)
{
    internal static readonly BackgroundActivitySnapshotBatch Empty = new(
        Array.Empty<BackgroundActivitySnapshot>(),
        Array.Empty<string>());
}
