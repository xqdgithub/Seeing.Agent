namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// 能力映射表：把 <see cref="ModelType"/>（模型自述的用途）与
/// <see cref="LlmCapabilityKind"/>（可调用的能力端口）双向对齐，并提供两种字符串投影。
/// <para>三张表 + 两套投影集中在本静态类，是仓库内唯一的 capability 字符串出处：
/// 调用点一律走 <see cref="ToConfigKey"/> / <see cref="ToHookValue"/>，禁止手拼字符串
/// （spec §7.2.1 / 决议 18）。本类是纯函数、零业务策略——它只解释"类型 ↔ 能力"的既定对应，
/// 不判断"该用哪个模型"（spec §13 原则 2）。</para>
/// </summary>
public static class LlmCapabilityMap
{
    /// <summary>除 <see cref="LlmCapabilityKind.None"/> 之外的全部单值能力，按枚举位顺序。</summary>
    private static readonly LlmCapabilityKind[] s_singleKindsWithoutNone =
        Enum.GetValues<LlmCapabilityKind>().Where(k => k != LlmCapabilityKind.None).ToArray();

    /// <summary>能力 → 配置键（小驼峰），用于 <see cref="ProviderConfig.Capabilities"/> 与 <c>DefaultModels</c> 的 key。</summary>
    private static readonly Dictionary<LlmCapabilityKind, string> s_configKeys = new()
    {
        [LlmCapabilityKind.None] = "none",
        [LlmCapabilityKind.Chat] = "chat",
        [LlmCapabilityKind.Embedding] = "embedding",
        [LlmCapabilityKind.Rerank] = "rerank",
        [LlmCapabilityKind.Decision] = "decision",
        [LlmCapabilityKind.SpeechSynthesis] = "speechSynthesis",
        [LlmCapabilityKind.SpeechTranscription] = "speechTranscription",
        [LlmCapabilityKind.ImageGeneration] = "imageGeneration",
        [LlmCapabilityKind.VideoGeneration] = "videoGeneration",
    };

    /// <summary>能力 → Hook 载荷值（点分），用于 <c>llm.*</c> 的 capability 字段。</summary>
    private static readonly Dictionary<LlmCapabilityKind, string> s_hookValues = new()
    {
        [LlmCapabilityKind.None] = "none",
        [LlmCapabilityKind.Chat] = "chat",
        [LlmCapabilityKind.Embedding] = "embedding",
        [LlmCapabilityKind.Rerank] = "rerank",
        [LlmCapabilityKind.Decision] = "decision",
        [LlmCapabilityKind.SpeechSynthesis] = "speech.synthesis",
        [LlmCapabilityKind.SpeechTranscription] = "speech.transcription",
        [LlmCapabilityKind.ImageGeneration] = "image.generation",
        [LlmCapabilityKind.VideoGeneration] = "video.generation",
    };

    /// <summary>ModelType → 能力（可能一对多：<see cref="ModelType.Speech"/> 同时含合成与转写）。</summary>
    private static readonly Dictionary<ModelType, LlmCapabilityKind[]> s_byModelType = new()
    {
        [ModelType.Text] = [LlmCapabilityKind.Chat],
        [ModelType.Embedding] = [LlmCapabilityKind.Embedding],
        [ModelType.Rerank] = [LlmCapabilityKind.Rerank],
        [ModelType.Decision] = [LlmCapabilityKind.Decision],
        [ModelType.Speech] = [LlmCapabilityKind.SpeechSynthesis, LlmCapabilityKind.SpeechTranscription],
        [ModelType.ImageGeneration] = [LlmCapabilityKind.ImageGeneration],
        [ModelType.Video] = [LlmCapabilityKind.VideoGeneration],
        [ModelType.Other] = [LlmCapabilityKind.None],
    };

    /// <summary>能力 → 客户端接口类型。<c>Resolve&lt;T&gt;</c> 的 T→Kind 反查唯一来源。</summary>
    public static IReadOnlyDictionary<LlmCapabilityKind, Type> ClientTypes { get; } =
        new Dictionary<LlmCapabilityKind, Type>
        {
            [LlmCapabilityKind.Chat] = typeof(IChatCompletionClient),
            [LlmCapabilityKind.Embedding] = typeof(IEmbeddingClient),
            [LlmCapabilityKind.Rerank] = typeof(IRerankClient),
            [LlmCapabilityKind.Decision] = typeof(IDecisionClient),
            [LlmCapabilityKind.SpeechSynthesis] = typeof(ISpeechSynthesisClient),
            [LlmCapabilityKind.SpeechTranscription] = typeof(ISpeechTranscriptionClient),
            [LlmCapabilityKind.ImageGeneration] = typeof(IImageGenerationClient),
            [LlmCapabilityKind.VideoGeneration] = typeof(IVideoGenerationClient),
        };

    /// <summary>
    /// <see cref="ModelType"/> 对应的能力集合。每个 ModelType 都有映射
    /// （<see cref="ModelType.Other"/> 映射为只含 <see cref="LlmCapabilityKind.None"/> 的集合）。
    /// </summary>
    public static IReadOnlyList<LlmCapabilityKind> FromModelType(ModelType type) =>
        s_byModelType.TryGetValue(type, out var kinds) ? kinds : [LlmCapabilityKind.None];

    /// <summary>协议 → 缺省能力（Provider 未配置 <c>capabilities</c> 时使用）。</summary>
    public static LlmCapabilityKind DefaultCapabilities(string protocol) =>
        ProviderCapabilityDefaults.Resolve(protocol);

    /// <summary>能力 → 配置键（小驼峰，如 <c>speechSynthesis</c>）。</summary>
    public static string ToConfigKey(LlmCapabilityKind kind) => Project(kind, s_configKeys);

    /// <summary>能力 → Hook 载荷值（点分，如 <c>speech.synthesis</c>）。</summary>
    public static string ToHookValue(LlmCapabilityKind kind) => Project(kind, s_hookValues);

    /// <summary>
    /// 单一能力查表投影；<paramref name="kind"/> 为复合位集时逐位展开并以逗号连接
    /// （仅供诊断呈现，配置解析端不接受这种形状）。未登记的位原样回落到 <c>ToString()</c>。
    /// </summary>
    private static string Project(LlmCapabilityKind kind, IReadOnlyDictionary<LlmCapabilityKind, string> table)
    {
        if (kind == LlmCapabilityKind.None)
            return table[LlmCapabilityKind.None];

        // 只有一个比特被置位 → 单值能力，直接查表
        if ((kind & (kind - 1)) == 0)
            return table.TryGetValue(kind, out var single) ? single : kind.ToString()!;

        var parts = s_singleKindsWithoutNone.Where(f => kind.HasFlag(f)).Select(f => table[f]).ToArray();
        return parts.Length > 0 ? string.Join(",", parts) : kind.ToString()!;
    }
}
