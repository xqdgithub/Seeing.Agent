namespace Seeing.Agent.Modules;

/// <summary>
/// 进程级结算宿主默认项（由 Host Shape 登记；无宿主时保持 null → 无 scenario 则 base 为空）。
/// </summary>
public sealed class ProcessSettlementOptions
{
    /// <summary>
    /// Host Shape 默认 scenario 名；当 <c>seeing.json</c> 未配置 <c>scenario</c> 时使用。
    /// </summary>
    public string? HostDefaultScenario { get; set; }
}
