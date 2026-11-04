namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// 语音转写端口（ASR，<see cref="LlmCapabilityKind.SpeechTranscription"/>）。
/// </summary>
public interface ISpeechTranscriptionClient : ILlmCapabilityClient
{
    /// <summary>非流式转写，返回全文与可选段落列表。</summary>
    Task<TranscriptionResult> TranscribeAsync(
        TranscriptionRequest request,
        LlmCallContext? call = null,
        CancellationToken ct = default);

    /// <summary>流式转写：按增量产出 <see cref="TranscriptSegment"/>。</summary>
    IAsyncEnumerable<TranscriptSegment> TranscribeStreamAsync(
        TranscriptionRequest request,
        LlmCallContext? call = null,
        CancellationToken ct = default);
}
