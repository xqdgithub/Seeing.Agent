using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm;

namespace Seeing.Agent.Core.Llm;

/// <summary>
/// 订阅 <see cref="ModelCapabilitiesChange"/>；仅当 <see cref="ModelCapabilitiesChange.InvalidateModelCatalog"/> 为 true 时刷新聚合目录。
/// </summary>
public sealed class ModelCapabilityCatalogReloadHandler : ReloadHandlerBase<ModelCapabilitiesChange>
{
    private readonly IModelConfigManager _modelConfigManager;

    public ModelCapabilityCatalogReloadHandler(IModelConfigManager modelConfigManager)
        => _modelConfigManager = modelConfigManager;

    public override string ComponentId => "model-capability-catalog";

    protected override Task ReloadAsync(ModelCapabilitiesChange change, CancellationToken ct)
    {
        if (!change.InvalidateModelCatalog)
            return Task.CompletedTask;

        return _modelConfigManager.RefreshCatalogAsync(null, ct);
    }
}
