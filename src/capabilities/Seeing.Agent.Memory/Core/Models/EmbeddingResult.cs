namespace Seeing.Agent.Memory.Core.Models;

/// <summary>
/// Embedding 结果
/// </summary>
/// <param name="Text">原始文本</param>
/// <param name="Vector">向量</param>
/// <param name="TokenCount">Token 数量</param>
/// <param name="ModelId">
/// 产出该向量的模型引用（形如 <c>provider/model</c>）。
/// <para>向量空间随模型而不同：缓存键必须带上它（spec §7.8.1），
/// 否则换模型后旧向量会被当作同一空间复用。本地生成的向量（如测试替身）可为 null。</para>
/// </param>
public record EmbeddingResult(
    string Text,
    float[] Vector,
    int TokenCount,
    string? ModelId = null
);
