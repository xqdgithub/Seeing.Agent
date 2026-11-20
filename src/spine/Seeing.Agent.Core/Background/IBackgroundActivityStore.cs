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
}
