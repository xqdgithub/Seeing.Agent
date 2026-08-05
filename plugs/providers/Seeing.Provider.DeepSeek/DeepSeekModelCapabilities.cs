using Seeing.Agent.Llm;

namespace Seeing.Provider.DeepSeek;

/// <summary>
/// DeepSeek 模型能力预置（List Models API 不返回 limit 等字段，用静态表覆盖）。
/// </summary>
public static class DeepSeekModelCapabilities
{
    /// <summary>
    /// 已知模型 Id → 能力模板。Key 大小写不敏感匹配。
    /// </summary>
    public static IReadOnlyDictionary<string, ModelConfig> Presets { get; } =
        new Dictionary<string, ModelConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["deepseek-v4-flash"] = Create(
                id: "deepseek-v4-flash",
                name: "DeepSeek V4 Flash",
                context: 1_000_000,
                output: 384_000,
                thinking: null),

            ["deepseek-v4-pro"] = Create(
                id: "deepseek-v4-pro",
                name: "DeepSeek V4 Pro",
                context: 1_000_000,
                output: 384_000,
                thinking: null),

            // 兼容别名：仍可能出现在 List Models；能力与 V4 Flash 对齐
            ["deepseek-chat"] = Create(
                id: "deepseek-chat",
                name: "DeepSeek Chat",
                context: 1_000_000,
                output: 384_000,
                thinking: null),

            ["deepseek-reasoner"] = Create(
                id: "deepseek-reasoner",
                name: "DeepSeek Reasoner",
                context: 1_000_000,
                output: 384_000,
                thinking: new ThinkingOptions { Type = "enabled" }),
        };

    /// <summary>
    /// 用预置能力覆盖 List Models 返回项的 Limit / Name / Options / Modalities / Types。
    /// 未知 Id 原样返回（仅保证 Provider）。
    /// </summary>
    public static ModelConfig Apply(ModelConfig listed)
    {
        ArgumentNullException.ThrowIfNull(listed);

        if (string.IsNullOrWhiteSpace(listed.Id) ||
            !Presets.TryGetValue(listed.Id, out var preset))
        {
            listed.Provider = string.IsNullOrWhiteSpace(listed.Provider) ? "deepseek" : listed.Provider;
            return listed;
        }

        return new ModelConfig
        {
            Id = listed.Id,
            Name = preset.Name ?? listed.Name ?? listed.Id,
            Provider = "deepseek",
            Types = preset.Types.Count > 0 ? [.. preset.Types] : listed.Types,
            Modalities = CloneModalities(preset.Modalities),
            Limit = new ModelLimits
            {
                Context = preset.Limit.Context,
                Output = preset.Limit.Output
            },
            Options = CloneOptions(preset.Options),
            Pricing = preset.Pricing is null
                ? null
                : new ModelPricing
                {
                    Input = preset.Pricing.Input,
                    Output = preset.Pricing.Output,
                    CacheRead = preset.Pricing.CacheRead,
                    CacheWrite = preset.Pricing.CacheWrite
                }
        };
    }

    public static IReadOnlyList<ModelConfig> ApplyAll(IEnumerable<ModelConfig> listed)
    {
        ArgumentNullException.ThrowIfNull(listed);
        return listed.Select(Apply).ToList();
    }

    private static ModelConfig Create(
        string id,
        string name,
        int context,
        int output,
        ThinkingOptions? thinking)
        => new()
        {
            Id = id,
            Name = name,
            Provider = "deepseek",
            Types = [ModelType.Text],
            Modalities = new ModelModalities
            {
                Input = ["text"],
                Output = ["text"]
            },
            Limit = new ModelLimits { Context = context, Output = output },
            Options = thinking is null
                ? null
                : new ModelOptions { Thinking = thinking }
        };

    private static ModelModalities CloneModalities(ModelModalities source)
        => new()
        {
            Input = [.. source.Input],
            Output = [.. source.Output]
        };

    private static ModelOptions? CloneOptions(ModelOptions? source)
    {
        if (source?.Thinking is null)
            return source is null ? null : new ModelOptions();

        return new ModelOptions
        {
            Thinking = new ThinkingOptions
            {
                Type = source.Thinking.Type,
                BudgetTokens = source.Thinking.BudgetTokens
            }
        };
    }
}
