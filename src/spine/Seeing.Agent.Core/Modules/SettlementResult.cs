namespace Seeing.Agent.Core.Modules;

/// <summary>进程级结算成功结果。</summary>
public sealed class SettlementResult
{
    /// <summary>
    /// 有效 Boot 指针（经 BootOverride &gt; ConfiguredBoot &gt; HostDefaultBoot &gt; <c>*</c> 解析后）。
    /// </summary>
    public required string Boot { get; init; }

    /// <summary>
    /// 进程默认工作模式名（仅诊断/日志）。<b>不</b>被 Activate / bootEnabled 消费。
    /// </summary>
    public required string? Scenario { get; init; }

    /// <summary>最终启用模块 id（= bootEnabled，稳定排序）。</summary>
    public required IReadOnlyList<string> Enabled { get; init; }

    /// <summary>
    /// 独占 seam 绑定（seam 名 → 提供方模块 id）。由 <c>Seams</c> + <c>HostDefaultSeams</c> 解析，
    /// 无硬编码通道名；提供方在各自 <c>ConfigureServices</c> 登记实现。
    /// </summary>
    public required IReadOnlyDictionary<string, string> BoundSeams { get; init; }

    /// <summary>告警消息（未知 id、Modules.Enabled 忽略等）；不阻止启动。</summary>
    public required IReadOnlyList<string> Warnings { get; init; }
}
