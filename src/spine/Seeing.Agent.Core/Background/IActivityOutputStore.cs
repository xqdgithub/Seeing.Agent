using Seeing.Agent.Abstractions.Background;

namespace Seeing.Agent.Core.Background;

/// <summary>
/// 活动输出存储端口（internal：只由 Core 内部与测试替身实现）。
/// <para>
/// 追加写 / 分页读 / 删除。截断与单页上限（32KB）由实现负责，manager 只做归属校验后转发。
/// </para>
/// </summary>
internal interface IActivityOutputStore
{
    /// <summary>
    /// 为某活动设定输出上限（字节），覆盖配置默认值。
    /// <para>由 manager 在活动创建时调用；未设定的活动回落到配置默认值。</para>
    /// </summary>
    void Configure(string activityId, long limitBytes);

    /// <summary>追加一段输出字节。</summary>
    Task AppendAsync(string activityId, ReadOnlyMemory<byte> chunk, CancellationToken ct);

    /// <summary>按偏移分页读取输出。</summary>
    Task<BackgroundActivityOutputPage> ReadAsync(string activityId, long offset, int maxBytes, CancellationToken ct);

    /// <summary>删除该活动的输出文件。</summary>
    Task DeleteAsync(string activityId);
}
