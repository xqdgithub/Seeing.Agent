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
}
