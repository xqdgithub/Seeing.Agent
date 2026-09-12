namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// FillEmpty 合并：仅填充目标仍「空」的字段。本类型为唯一实现。
/// </summary>
public static class ModelCapabilityFillEmpty
{
    /// <summary>
    /// <see cref="ModelLimits"/> 类型默认值。FillEmpty 将 4096 视为未设置（default means unset for FillEmpty）。
    /// </summary>
    public const int DefaultLimitUnset = 4096;

    /// <summary>
    /// 将 <paramref name="source"/> 中非空字段填入 <paramref name="target"/> 仍空的位置；原地修改并返回 target。
    /// </summary>
    public static ModelConfig Apply(ModelConfig target, ModelCapabilityEntry source)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);

        if (string.IsNullOrWhiteSpace(target.Name) && !string.IsNullOrWhiteSpace(source.Name))
            target.Name = source.Name;

        if (source.Limit is not null)
        {
            target.Limit ??= new ModelLimits();
            if (target.Limit.Context == DefaultLimitUnset && source.Limit.Context is int ctx)
                target.Limit.Context = ctx;
            if (target.Limit.Output == DefaultLimitUnset && source.Limit.Output is int output)
                target.Limit.Output = output;
        }

        if (IsModalitiesEmpty(target.Modalities) && source.Modalities is not null)
            target.Modalities = CloneModalities(source.Modalities);

        if (NeedsThinkingFill(target.Options?.Thinking) &&
            source.Options?.Thinking is { } thinking)
        {
            target.Options ??= new ModelOptions();
            target.Options.Thinking = CloneThinking(thinking);
        }

        if (target.Pricing is null && source.Pricing is not null)
            target.Pricing = ClonePricing(source.Pricing);

        return target;
    }

    private static bool IsModalitiesEmpty(ModelModalities? modalities)
        => modalities is null
           || (modalities.Input.Count == 0 && modalities.Output.Count == 0);

    private static bool NeedsThinkingFill(ThinkingOptions? thinking)
        => thinking is null
           || thinking.Supported != true
           || thinking.Levels is null
           || thinking.Levels.Count == 0;

    private static ModelModalities CloneModalities(ModelModalities source)
        => new()
        {
            Input = [.. source.Input],
            Output = [.. source.Output]
        };

    private static ThinkingOptions CloneThinking(ThinkingOptions source)
        => new()
        {
            Supported = source.Supported,
            Default = source.Default,
            Type = source.Type,
            BudgetTokens = source.BudgetTokens,
            Interleaved = source.Interleaved,
            Levels = source.Levels?.Select(l => new ThinkingLevel
            {
                Key = l.Key,
                Label = l.Label,
                BudgetTokens = l.BudgetTokens
            }).ToList()
        };

    private static ModelPricing ClonePricing(ModelPricing source)
        => new()
        {
            Input = source.Input,
            Output = source.Output,
            CacheRead = source.CacheRead,
            CacheWrite = source.CacheWrite
        };
}
