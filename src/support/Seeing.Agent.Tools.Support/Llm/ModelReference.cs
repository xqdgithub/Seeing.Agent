using Seeing.Agent.Llm;

namespace Seeing.Agent.Tools.Support.Llm;

/// <summary>
/// 配置里的模型引用（<c>provider/model</c>）→ 能力端口实际发送的裸模型 ID。
/// </summary>
/// <remarks>
/// 与 <c>Seeing.Agent.Memory</c> 的 <c>EmbeddingModelReference</c> 同口径：
/// 目录里有该模型时以 <c>ModelConfig.Id</c> 为准（可能与目录键不同）；
/// 查不到时剥离 <c>provider/</c> 前缀。本方法不做任何「挑模型」的工作。
/// </remarks>
public static class ModelReference
{
    /// <summary>引用 → 出站模型 ID。</summary>
    public static string ToWireModelId(IModelConfigManager models, string modelRef)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelRef);

        var id = models.GetModel(modelRef)?.Id;
        if (!string.IsNullOrWhiteSpace(id))
            return id;

        var slash = modelRef.LastIndexOf('/');
        return slash >= 0 && slash < modelRef.Length - 1
            ? modelRef[(slash + 1)..]
            : modelRef;
    }
}
