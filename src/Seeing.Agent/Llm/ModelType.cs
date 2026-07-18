namespace Seeing.Agent.Llm;

/// <summary>
/// 模型用途类型（多标签）。缺省视为 <see cref="Text"/>。
/// </summary>
public enum ModelType
{
    Text,
    Embedding,
    Rerank,
    Image,
    Video,
    Speech,
    Other
}
