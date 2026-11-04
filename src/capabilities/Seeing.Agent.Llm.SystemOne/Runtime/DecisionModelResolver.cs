using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Agent.Llm.SystemOne.Runtime;

/// <summary>
/// Decision 模型解析（spec §7.9 的四步链，本包内实现）。
/// <para>顺序固定：① 调用方显式传入 → ② <c>DefaultModels.decision</c>（唯一消费点）→
/// ③ 目录中恰好一个 Decision 模型 → ④ 报错。</para>
/// <para><b>不静默回落</b>：多个候选时报错并列出候选（决议 13 / spec §13 原则 1）。
/// <c>Tools.SystemOne</c> 因不得引用本能力包，按同一顺序各自实现，两处文案保持一致。</para>
/// </summary>
internal static class DecisionModelResolver
{
    /// <summary>无任何 Decision 模型时的错误文案。</summary>
    public const string NoModelMessage =
        "未配置 Decision 模型：请显式传入 model，或在设置中指定 DefaultModels.decision";

    /// <summary>解析出本次调用使用的模型 ID。</summary>
    public static string Resolve(IModelConfigManager models, string? explicitModelId)
    {
        ArgumentNullException.ThrowIfNull(models);

        if (!string.IsNullOrWhiteSpace(explicitModelId))
            return explicitModelId!;

        var configuredDefault = models.GetDefaultModel(LlmCapabilityKind.Decision);
        if (!string.IsNullOrWhiteSpace(configuredDefault))
            return configuredDefault!;

        var candidates = models.GetModelsByType(ModelType.Decision)
            .Keys
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        if (candidates.Count == 1)
            return candidates[0];

        if (candidates.Count == 0)
            throw new InvalidOperationException(NoModelMessage);

        throw new InvalidOperationException(
            $"未指定 Decision 模型，且模型目录中存在 {candidates.Count} 个候选："
            + $"{string.Join(", ", candidates)}；请显式传入 model，或在设置中指定 DefaultModels.decision");
    }
}
