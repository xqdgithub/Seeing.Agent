using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Agent.Llm.Support;

/// <summary>
/// 依据模型 ID 的强命名约定推断用途类型。
/// <para>远端 <c>/v1/models</c> 通常不返回用途类型，插件映射时可据此兜底标注，
/// 减少用户手工修正量；未命中约定返回 null（不臆测）。</para>
/// <para>用户级 <c>ModelOverrides</c> 覆盖始终优先于本推断。</para>
/// <para>本类型属启发式实现，故置于 Support 层而非 Abstractions（后者零实现纪律）。</para>
/// </summary>
public static class ModelTypeInference
{
    /// <summary>按强约定推断用途类型；无匹配返回 null。</summary>
    public static List<ModelType>? InferTypes(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return null;

        var id = modelId.ToLowerInvariant();

        if (ContainsAny(id, "embedding", "embed-", "-embed", "bge-m3", "bge-large", "gte-", "text-embedding"))
            return [ModelType.Embedding];

        if (ContainsAny(id, "rerank", "reranker", "bge-reranker"))
            return [ModelType.Rerank];

        if (ContainsAny(id, "whisper", "-tts", "tts-", "speech", "cosyvoice", "sensevoice"))
            return [ModelType.Speech];

        if (ContainsAny(id, "stable-diffusion", "sdxl", "flux", "dall-e", "kolors", "-image", "image-"))
            return [ModelType.Image];

        if (ContainsAny(id, "cogvideo", "wan2", "-video", "video-"))
            return [ModelType.Video];

        return null;
    }

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
