namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// Manager 投影的源注册信息（含配置 Order/Enabled 与能力标志）。
/// </summary>
public sealed class ModelCapabilitySourceRegistration
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public int Order { get; init; }
    public bool Enabled { get; init; }
    public bool Registered { get; init; }
    public ModelCapabilitySourceStatus? Status { get; init; }
    public bool CanList { get; init; }
    public bool CanEdit { get; init; }
    public bool CanRefresh { get; init; }
    public bool CanBatchEdit { get; init; }
}
