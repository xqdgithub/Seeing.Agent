using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Agent.Core.Llm;

/// <summary>
/// 模型 ↔ 能力覆盖判定 — 全项目唯一实现。
/// <para>
/// <b>为什么要集中：</b>「这个模型能不能承担该能力」在两处被判定过：
/// <see cref="ModelConfigManager"/> 在「设为默认模型」写入时校验，
/// <see cref="LlmRouter"/> 在每次路由时校验（第 3 层）。两份实现若漂移，
/// 就会出现「设置时通过、运行时被 Router 拒绝」（或反之）——只在运行期暴露的错配。
/// 语音尤其容易错：<c>Speech</c> 类型同时覆盖合成与转写，必须靠 <c>Modalities</c> 方向消歧
/// （合成看 <c>output</c>、转写看 <c>input</c>，决议 24）。
/// </para>
/// </summary>
internal static class ModelCapabilityPredicate
{
    /// <summary>
    /// 模型有效类型经 <see cref="LlmCapabilityMap.FromModelType"/> 投影后是否覆盖所需能力
    /// （复合位要求逐位覆盖），并补充语音的模态方向约束。
    /// </summary>
    /// <param name="config">模型配置（读取 <see cref="ModelConfig.Types"/> 与 <see cref="ModelConfig.Modalities"/>）。</param>
    /// <param name="capability">所需能力（可为复合位集）。</param>
    /// <param name="effectiveTypes">
    /// 模型有效类型。传 null 时按 <see cref="ModelTypeRules.GetEffectiveTypes"/> 推导；
    /// 调用方若已缓存推导结果可传入以避免重复计算。
    /// </param>
    public static bool Covers(
        ModelConfig config,
        LlmCapabilityKind capability,
        IReadOnlyList<ModelType>? effectiveTypes = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        var types = effectiveTypes ?? ModelTypeRules.GetEffectiveTypes(config);
        var modelKinds = types
            .SelectMany(LlmCapabilityMap.FromModelType)
            .Where(kind => kind != LlmCapabilityKind.None)
            .ToHashSet();

        foreach (var required in SingleBits(capability))
        {
            if (!modelKinds.Contains(required))
                return false;
        }

        return CoversSpeechModality(config, capability);
    }

    /// <summary>
    /// 语音能力的模态检查：<c>Speech</c> 类型同时覆盖合成与转写，必须靠 <c>Modalities</c> 消歧。
    /// <para>非语音能力一律通过（此项只补充语音的方向性约束）。</para>
    /// </summary>
    public static bool CoversSpeechModality(ModelConfig config, LlmCapabilityKind capability)
    {
        var isSynthesis = capability.HasFlag(LlmCapabilityKind.SpeechSynthesis);
        var isTranscription = capability.HasFlag(LlmCapabilityKind.SpeechTranscription);

        if (!isSynthesis && !isTranscription)
            return true;

        var modalities = isSynthesis ? config.Modalities?.Output : config.Modalities?.Input;
        return modalities is not null
               && modalities.Any(value => string.Equals(value, "audio", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>能力位集拆成单值位序列（枚举是唯一出处，不手拼名字）。</summary>
    public static IEnumerable<LlmCapabilityKind> SingleBits(LlmCapabilityKind capability)
    {
        foreach (var kind in Enum.GetValues<LlmCapabilityKind>())
        {
            if (kind != LlmCapabilityKind.None &&
                (kind & (kind - 1)) == 0 &&
                capability.HasFlag(kind))
            {
                yield return kind;
            }
        }
    }
}
