namespace Seeing.Agent.Modules;

/// <summary>进程级结算成功结果。</summary>
public sealed class SettlementResult
{
    /// <summary>解析后的 scenario 名（可能为 null，表示无场景、base 为空）。</summary>
    public required string? Scenario { get; init; }

    /// <summary>最终启用模块 id（稳定排序）。</summary>
    public required IReadOnlyList<string> Enabled { get; init; }

    /// <summary>
    /// 独占 seam 绑定（seam 名 → 提供方模块 id）。由 scenario/user <c>seams.*</c> 解析，
    /// 无硬编码通道名；提供方在各自 <c>ConfigureServices</c> 登记实现。
    /// </summary>
    public required IReadOnlyDictionary<string, string> BoundSeams { get; init; }

    /// <summary>告警消息（未知 id、场景缺模块等）；不阻止启动。</summary>
    public required IReadOnlyList<string> Warnings { get; init; }
}
