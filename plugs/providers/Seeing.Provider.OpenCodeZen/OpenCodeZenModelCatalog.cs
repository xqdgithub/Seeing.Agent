namespace Seeing.Provider.OpenCodeZen;

/// <summary>
/// OpenCode Zen 私有模型条目（插件内部元数据，不受系统 ModelConfig 数据结构约束）。
/// </summary>
public sealed class OpenCodeZenModel
{
    /// <summary>模型 ID（API 调用标识，如 nemotron-3-ultra-free）</summary>
    public required string Id { get; init; }

    /// <summary>显示名称</summary>
    public required string Name { get; init; }

    /// <summary>是否免费模型（无需 API Key）</summary>
    public bool IsFree { get; init; }

    /// <summary>上下文窗口大小</summary>
    public int Context { get; init; } = OpenCodeZenModelCatalog.DefaultContext;

    /// <summary>最大输出 Token 数</summary>
    public int Output { get; init; } = OpenCodeZenModelCatalog.DefaultOutput;

    /// <summary>是否支持图片输入</summary>
    public bool SupportsImage { get; init; }

    /// <summary>输入价格（美元/百万 Token；免费模型为 0，未知为 null）</summary>
    public double? InputPrice { get; init; }

    /// <summary>输出价格（美元/百万 Token；免费模型为 0，未知为 null）</summary>
    public double? OutputPrice { get; init; }
}

/// <summary>
/// OpenCode Zen 模型目录：免费判定；能力（limit/thinking）由 <c>IModelCapabilityManager</c> FillEmpty 补全。
/// </summary>
public static class OpenCodeZenModelCatalog
{
    /// <summary>未知模型默认上下文（与 ModelLimits 缺省一致，便于 FillEmpty）</summary>
    public const int DefaultContext = 4096;

    /// <summary>未知模型默认最大输出（与 ModelLimits 缺省一致，便于 FillEmpty）</summary>
    public const int DefaultOutput = 4096;

    private const string FreeSuffix = "-free";

    /// <summary>
    /// 免费模型显式 ID 集合（不满足 -free 后缀规则、需显式声明的模型）。
    /// </summary>
    private static readonly HashSet<string> ExplicitFreeIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "big-pickle"
    };

    /// <summary>
    /// 判定模型是否免费：id 以 "-free" 结尾，或命中显式免费集合。
    /// </summary>
    public static bool IsFreeModel(string id)
        => !string.IsNullOrWhiteSpace(id)
           && (id.EndsWith(FreeSuffix, StringComparison.OrdinalIgnoreCase)
               || ExplicitFreeIds.Contains(id));

    /// <summary>
    /// 能力预置已迁出；此处仅规范化免费标记，不写 limit。
    /// </summary>
    public static OpenCodeZenModel ApplyPreset(OpenCodeZenModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!IsFreeModel(model.Id))
            return model;

        return new OpenCodeZenModel
        {
            Id = model.Id,
            Name = model.Name,
            IsFree = true,
            Context = model.Context,
            Output = model.Output,
            SupportsImage = model.SupportsImage,
            InputPrice = 0,
            OutputPrice = 0
        };
    }

    /// <summary>
    /// 应用用户自定义能力覆盖（Key 大小写不敏感）；优先级高于内置预置表。
    /// 用于模型列表调整时无需改代码即可更新能力。
    /// </summary>
    public static OpenCodeZenModel ApplyOverrides(
        OpenCodeZenModel model,
        IReadOnlyDictionary<string, ModelCapabilityOverride>? overrides)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (overrides is null || overrides.Count == 0)
            return model;

        var key = overrides.Keys.FirstOrDefault(
            k => string.Equals(k, model.Id, StringComparison.OrdinalIgnoreCase));
        if (key is null || !overrides.TryGetValue(key, out var overrideConfig))
            return model;

        var isFree = overrideConfig.IsFree ?? model.IsFree;

        return new OpenCodeZenModel
        {
            Id = model.Id,
            Name = model.Name,
            IsFree = isFree,
            Context = overrideConfig.Context,
            Output = overrideConfig.Output,
            SupportsImage = model.SupportsImage,
            // 手动标记为免费时，价格按 0 处理；否则保留原价格
            InputPrice = isFree ? 0 : model.InputPrice,
            OutputPrice = isFree ? 0 : model.OutputPrice
        };
    }
}
