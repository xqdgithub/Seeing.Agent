using Seeing.Agent.Llm;

namespace Seeing.Agent.Memory.Core.Embedding;

/// <summary>
/// 配置里的向量模型引用 → 能力端口实际使用的裸模型 ID。
/// <para>目录里有该模型时以其 <c>Id</c> 为准（可能与目录键不同）；查不到时退化为剥离
/// <c>provider/</c> 前缀。<b>本方法不做任何“挑模型”的工作</b>：传入的引用一定是用户显式配置的
/// <c>Embedding.ModelId</c>（spec §0 决议 10）。</para>
/// </summary>
internal static class EmbeddingModelReference
{
    internal static string ToWireModelId(IModelConfigManager models, string modelRef)
    {
        var id = models.GetModel(modelRef)?.Id;
        if (!string.IsNullOrWhiteSpace(id))
            return id;

        var slash = modelRef.LastIndexOf('/');
        return slash >= 0 && slash < modelRef.Length - 1
            ? modelRef[(slash + 1)..]
            : modelRef;
    }
}
