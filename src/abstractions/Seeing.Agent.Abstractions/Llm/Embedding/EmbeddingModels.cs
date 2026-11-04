namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// 向量化请求（spec §7.2.2）。
/// </summary>
public sealed record EmbeddingRequest
{
    /// <summary>模型 ID（裸 ID，Provider 前缀由 Router 剥离后传入）。</summary>
    public required string Model { get; init; }

    /// <summary>待编码文本，按输入顺序回向量。</summary>
    public required IReadOnlyList<string> Inputs { get; init; }

    /// <summary>期望向量维度；仅支持该参数的模型生效（如 text-embedding-3-*）。</summary>
    public int? Dimensions { get; init; }

    /// <summary>编码格式（如 <c>float</c> / <c>base64</c>）。</summary>
    public string? EncodingFormat { get; init; }
}

/// <summary>
/// 单条向量。<paramref name="Index"/> 是服务端回显的序号，乱序响应以此为准排序。
/// </summary>
/// <param name="Text">与 <see cref="EmbeddingRequest.Inputs"/> 对应的原文。</param>
/// <param name="Values">向量分量。</param>
/// <param name="Index">输入序号。</param>
public sealed record EmbeddingVector(string Text, float[] Values, int Index);

/// <summary>
/// 向量化响应。
/// </summary>
public sealed record EmbeddingResponse
{
    /// <summary>向量集合，实现方须按 <see cref="EmbeddingVector.Index"/> 升序产出。</summary>
    public required IReadOnlyList<EmbeddingVector> Vectors { get; init; }

    /// <summary>向量维度（取首条）；空响应为 0。</summary>
    public int Dimensions => Vectors.Count > 0 ? Vectors[0].Values.Length : 0;

    /// <summary>token 用量；只进 <c>llm.after_complete</c> 载荷，不写会话预算（决议 1）。</summary>
    public TokenUsage? Usage { get; init; }
}
