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

    /// <summary>
    /// 追加一段输出字节；返回写入后的累计统计（实际落盘字节与是否已截断）。
    /// <para>返回值是快照 <c>OutputBytes</c> / <c>OutputTruncated</c> 的唯一来源：截断时实际写入少于传入字节，调用方不得自行累计。</para>
    /// </summary>
    Task<ActivityOutputAppendResult> AppendAsync(string activityId, ReadOnlyMemory<byte> chunk, CancellationToken ct);

    /// <summary>按偏移分页读取输出。</summary>
    Task<BackgroundActivityOutputPage> ReadAsync(string activityId, long offset, int maxBytes, CancellationToken ct);

    /// <summary>删除该活动的输出文件。</summary>
    Task DeleteAsync(string activityId);
}

/// <summary>
/// 一次输出追加的累计统计（internal：只在 Core 与测试替身之间传递）。
/// </summary>
/// <param name="TotalBytes">已实际落盘的输出字节数（不含截断标记）。</param>
/// <param name="Truncated">输出是否已因达到上限被截断。</param>
internal readonly record struct ActivityOutputAppendResult(long TotalBytes, bool Truncated);
