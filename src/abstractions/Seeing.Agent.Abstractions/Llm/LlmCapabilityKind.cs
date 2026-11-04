namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// LLM 能力种类（位集）。
/// <para>与「协议」（<see cref="ProviderConfig.Type"/>）正交：协议决定用什么方式说话，
/// 能力决定说哪种类型的话。一个 Provider 可同时供出多种能力。</para>
/// <para>位于 Abstractions 且为封闭枚举：插件无法新增能力值，要加新能力必须改本包并发版
/// （spec §13 原则 3 的已知限制）。</para>
/// </summary>
[Flags]
public enum LlmCapabilityKind
{
    /// <summary>无能力（仅作为空位集与 <see cref="ModelType.Other"/> 的投影）。</summary>
    None = 0,

    /// <summary>对话补全。</summary>
    Chat = 1 << 0,

    /// <summary>向量化（嵌入）。</summary>
    Embedding = 1 << 1,

    /// <summary>检索重排。</summary>
    Rerank = 1 << 2,

    /// <summary>结构化决策（Jev 三原语：noul / choice / score）。</summary>
    Decision = 1 << 3,

    /// <summary>语音合成（TTS）。</summary>
    SpeechSynthesis = 1 << 4,

    /// <summary>语音转写（ASR）。</summary>
    SpeechTranscription = 1 << 5,

    /// <summary>图像生成。</summary>
    ImageGeneration = 1 << 6,

    /// <summary>视频生成。</summary>
    VideoGeneration = 1 << 7,
}
