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

    /// <summary>
    /// 注入模型配置管理器构造聚合目录重载处理器。
    /// </summary>
    public ModelCapabilityCatalogReloadHandler(IModelConfigManager modelConfigManager)
        => _modelConfigManager = modelConfigManager;

    /// <summary>
    /// 热重载组件标识：model-capability-catalog。
    /// </summary>
    public override string ComponentId => "model-capability-catalog";

    /// <summary>
    /// 能力变更声明目录失效时刷新聚合模型目录，否则跳过。
    /// </summary>
    protected override Task ReloadAsync(ModelCapabilitiesChange change, CancellationToken ct)
    {
        if (!change.InvalidateModelCatalog)
            return Task.CompletedTask;

        return _modelConfigManager.RefreshCatalogAsync(null, ct);
    }
}
