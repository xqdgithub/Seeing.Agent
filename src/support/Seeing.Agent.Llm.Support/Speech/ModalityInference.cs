using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Agent.Llm.Support.Speech;

/// <summary>
/// 语音模态推断：按模型 ID 的强命名约定补齐 <see cref="ModelModalities"/>。
/// <para><b>为什么需要它</b>：Provider 的远端 <c>/models</c> 只返回 ID，不返回模态。
/// 而 Router 第 3 层对语音能力要求 <c>modalities.input</c>（转写）或 <c>modalities.output</c>（合成）
/// 明确含 <c>audio</c>，缺了就拒绝（<c>LlmRouter.ResolveCore</c> 决议 24）。
/// 不补的话，刷新出来的 <c>SenseVoiceSmall</c> 永远无法设为语音转写默认模型。</para>
/// <para><b>只在不歧义时填</b>：<see cref="ModelType.Speech"/> 同时覆盖合成与转写两个方向，
/// 必须靠名字判定；判不出就不填，让用户在模型页显式补——猜错会让合成模型被当转写模型路由通过，
/// 比空着更坏（与 Router「不猜」的立场一致）。</para>
/// </summary>
public static class ModalityInference
{
    /// <summary>音频模态标识（<c>ModelConfig.Modalities</c> 的取值约定，spec §7.10）。</summary>
    public const string AudioModality = "audio";

    /// <summary>语音转写方向的关键词（输入音频、输出文本）。</summary>
    private static readonly string[] s_transcriptionTokens =
        ["asr", "transcri", "whisper", "sensevoice", "stt"];

    /// <summary>语音合成方向的关键词（输入文本、输出音频）。</summary>
    private static readonly string[] s_synthesisTokens =
        ["tts", "cosyvoice", "tsts"];

    /// <summary>
    /// 依据模型 ID 与已推断的用途类型给出模态；无法确定时返回 <c>null</c>（不臆测）。
    /// </summary>
    /// <param name="modelId">模型 ID。</param>
    /// <param name="types">已推断/已声明的用途类型。</param>
    public static ModelModalities? Infer(string? modelId, IReadOnlyList<ModelType>? types)
    {
        if (types is null || types.Count == 0)
            return null;

        // 语音：必须按名字消歧（Speech 同时覆盖合成与转写）
        if (types.Contains(ModelType.Speech))
        {
            var id = (modelId ?? string.Empty).ToLowerInvariant();

            if (ContainsAny(id, s_transcriptionTokens))
                return Create(["audio"], ["text"]);

            if (ContainsAny(id, s_synthesisTokens))
                return Create(["text"], ["audio"]);

            return null;
        }

        if (types.Contains(ModelType.Embedding))
            return Create(["text"], ["embedding"]);

        if (types.Contains(ModelType.Rerank))
            return Create(["text"], ["text"]);

        if (types.Contains(ModelType.ImageGeneration))
            return Create(["text"], ["image"]);

        if (types.Contains(ModelType.Video))
            return Create(["text"], ["video"]);

        // 文本/决策等无特殊模态约定：不填，交给内建目录与用户覆盖
        return null;
    }

    /// <summary>
    /// 只在目标模态为空时写入推断值（不覆盖已有声明）。
    /// </summary>
    public static ModelConfig Apply(ModelConfig model)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (!IsEmpty(model.Modalities))
            return model;

        var inferred = Infer(model.Id, model.Types);
        if (inferred is not null)
            model.Modalities = inferred;

        return model;
    }

    /// <summary>模态是否为空（输入与输出都没有值）。</summary>
    public static bool IsEmpty(ModelModalities? modalities)
        => modalities is null
           || (modalities.Input.Count == 0 && modalities.Output.Count == 0);

    private static ModelModalities Create(List<string> input, List<string> output) => new()
    {
        Input = input,
        Output = output,
    };

    private static bool ContainsAny(string value, params string[] tokens)
    {
        foreach (var token in tokens)
        {
            if (value.Contains(token, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
