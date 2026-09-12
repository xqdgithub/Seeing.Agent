using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Provider.DeepSeek;

/// <summary>
/// DeepSeek 静态能力表（遗留，仅供单测）。生产 List Models 经 <c>IModelCapabilityManager</c> FillEmpty。
/// </summary>
[Obsolete("生产路径请用 IModelCapabilityManager；本类仅保留单测。")]
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
                thinking: DeepSeekThinkingLevels()),

            ["deepseek-v4-pro"] = Create(
                id: "deepseek-v4-pro",
                name: "DeepSeek V4 Pro",
                context: 1_000_000,
                output: 384_000,
                thinking: DeepSeekThinkingLevels()),

            // 兼容别名：仍可能出现在 List Models；能力与 V4 Flash 对齐
            ["deepseek-flash"] = Create(
                id: "deepseek-flash",
                name: "deepseek-flash",
                context: 1_000_000,
                output: 384_000,
                thinking: DeepSeekThinkingLevels()),
        };

    private static ThinkingOptions DeepSeekThinkingLevels() => new()
    {
        Supported = true,
        Interleaved = "reasoning_content",
        Levels =
        [
            new ThinkingLevel { Key = "disabled", Label = "关闭" },
            new ThinkingLevel { Key = "high", Label = "高" },
            new ThinkingLevel { Key = "max", Label = "最大" }
        ]
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
                Supported = source.Thinking.Supported,
                Default = source.Thinking.Default,
                Interleaved = source.Thinking.Interleaved,
                Levels = source.Thinking.Levels?.Select(l => new ThinkingLevel
                {
                    Key = l.Key,
                    Label = l.Label,
                    BudgetTokens = l.BudgetTokens
                }).ToList(),
                Type = source.Thinking.Type,
                BudgetTokens = source.Thinking.BudgetTokens
            }
        };
    }
}
