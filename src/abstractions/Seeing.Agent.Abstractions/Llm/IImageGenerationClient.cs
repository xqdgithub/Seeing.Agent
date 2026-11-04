namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// 图像生成端口（<see cref="LlmCapabilityKind.ImageGeneration"/>）。
/// 本次只出契约，无可跑实现（spec §7.11）。
/// </summary>
public interface IImageGenerationClient : ILlmCapabilityClient
{
    /// <summary>按提示词生成图像。</summary>
    Task<ImageGenerationResult> GenerateAsync(
        ImageGenerationRequest request,
        LlmCallContext? call = null,
        CancellationToken ct = default);
}
