namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// 模型用途类型（多标签）。缺省视为 <see cref="Text"/>。
/// <para><b>原 <see cref="Image"/> 已删除</b>（spec §7.12 / 决议 4）：存量语义就是"生成"，
/// 统一为 <see cref="ImageGeneration"/>；图像理解不占枚举值，由
/// <c>ModelConfig.Modalities.Input</c> 含 <c>image</c> 表达。</para>
/// </summary>
public enum ModelType
{
    /// <summary>文本对话/补全。</summary>
    Text,

    /// <summary>向量化（嵌入）。</summary>
    Embedding,

    /// <summary>检索重排。</summary>
    Rerank,

    /// <summary>结构化决策（Jev 三原语）。</summary>
    Decision,

    /// <summary>图像生成（取代原 <c>Image</c>）。</summary>
    ImageGeneration,

    /// <summary>视频生成。</summary>
    Video,

    /// <summary>语音（合成与/或转写，由 <c>Modalities</c> 消歧）。</summary>
    Speech,

    /// <summary>其他（不对应任何可路由能力）。</summary>
    Other
}
