using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Agent.WebUI.Components.Models;

/// <summary>
/// 能力在 WebUI 上的呈现辅助：界面标签、位集文案、以及能力 → 模型类型过滤的反查。
/// <para>本类只做展示投影，不含任何"该用哪个模型"的策略（spec §13 原则 1）；
/// 配置键的唯一出处仍是 <see cref="LlmCapabilityMap.ToConfigKey"/>，禁止手拼字符串。</para>
/// </summary>
public static class CapabilityLabels
{
    /// <summary>能力 → 中文名（纯界面文案；与配置键、Hook 载荷值无关）。</summary>
    private static readonly IReadOnlyDictionary<LlmCapabilityKind, string> s_chinese =
        new Dictionary<LlmCapabilityKind, string>
        {
            [LlmCapabilityKind.Chat] = "对话",
            [LlmCapabilityKind.Embedding] = "向量",
            [LlmCapabilityKind.Rerank] = "重排",
            [LlmCapabilityKind.Decision] = "决策",
            [LlmCapabilityKind.SpeechSynthesis] = "语音合成",
            [LlmCapabilityKind.SpeechTranscription] = "语音转写",
            [LlmCapabilityKind.ImageGeneration] = "图像生成",
            [LlmCapabilityKind.VideoGeneration] = "视频生成",
        };

    /// <summary>除 <see cref="LlmCapabilityKind.None"/> 之外的全部能力（界面展示顺序唯一来源）。</summary>
    public static IReadOnlyList<LlmCapabilityKind> All { get; } =
        Enum.GetValues<LlmCapabilityKind>().Where(k => k != LlmCapabilityKind.None).ToArray();

    /// <summary>界面标签：中文 + 配置键，如 <c>对话（chat）</c>。</summary>
    public static string Label(LlmCapabilityKind kind)
    {
        var chinese = s_chinese.TryGetValue(kind, out var name) ? name : kind.ToString()!;
        return $"{chinese}（{LlmCapabilityMap.ToConfigKey(kind)}）";
    }

    /// <summary>位集 → <c>chat, embedding</c> 形式的配置键串；空位集返回空串。</summary>
    public static string Describe(LlmCapabilityKind mask) =>
        string.Join(", ", Singles(mask).Select(LlmCapabilityMap.ToConfigKey));

    /// <summary>位集 → 单值能力序列（不含 <see cref="LlmCapabilityKind.None"/>，按枚举位序）。</summary>
    public static IReadOnlyList<LlmCapabilityKind> Singles(LlmCapabilityKind mask) =>
        All.Where(kind => mask.HasFlag(kind)).ToArray();

    /// <summary>模型有效类型（多标签）→ 能力位集。</summary>
    public static LlmCapabilityKind MaskOfTypes(IEnumerable<ModelType> types) =>
        types
            .SelectMany(LlmCapabilityMap.FromModelType)
            .Aggregate(LlmCapabilityKind.None, (mask, kind) => mask | kind);

    /// <summary>
    /// 能力 → 模型选择器的类型过滤，由 <see cref="LlmCapabilityMap.FromModelType"/> 反推，
    /// 避免 UI 侧另立一张类型表（spec §7.2.1 唯一出处纪律）。无对应类型时返回 null（不过滤）。
    /// </summary>
    public static ModelType? FilterTypeFor(LlmCapabilityKind kind) =>
        Enum.GetValues<ModelType>()
            .Where(t => t != ModelType.Other)
            .Select(t => (ModelType?)t)
            .FirstOrDefault(t => LlmCapabilityMap.FromModelType(t!.Value).Contains(kind));
}
