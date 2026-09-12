using Seeing.Agent.Abstractions.Configuration;

namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// 模型能力变更信号（经 <see cref="IReloadSignalBus"/> 发布）。
/// </summary>
public sealed class ModelCapabilitiesChange : IReloadSignal
{
    public ModelCapabilitiesChangeReason Reason { get; init; }
    public IReadOnlyList<string> AffectedSourceIds { get; init; } = [];
    public ModelCapabilitySourceChangeKind? SourceKind { get; init; }

    /// <summary>
    /// SSOT：是否刷聚合目录。Handler 只读本字段，禁止再读 Options。
    /// </summary>
    public bool InvalidateModelCatalog { get; init; }
}
