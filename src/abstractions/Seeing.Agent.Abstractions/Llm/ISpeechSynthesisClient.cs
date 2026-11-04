namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// 语音合成端口（TTS，<see cref="LlmCapabilityKind.SpeechSynthesis"/>）。
/// </summary>
public interface ISpeechSynthesisClient : ILlmCapabilityClient
{
    /// <summary>非流式合成，返回完整音频字节。</summary>
    Task<SpeechSynthesisResult> SynthesizeAsync(
        SpeechSynthesisRequest request,
        LlmCallContext? call = null,
        CancellationToken ct = default);

    /// <summary>流式合成：按响应体自然分块产出 <see cref="AudioChunk"/>。</summary>
    IAsyncEnumerable<AudioChunk> SynthesizeStreamAsync(
        SpeechSynthesisRequest request,
        LlmCallContext? call = null,
        CancellationToken ct = default);
}
