namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// 音频流分块。音频用独立类型，不复用 <see cref="StreamUpdate"/>（spec §7.2.2）。
/// </summary>
/// <param name="Data">分块字节（所有权归接收方，不要求实现侧释放）。</param>
/// <param name="MimeType">该块的 MIME 类型（如 <c>audio/mpeg</c>）。</param>
public sealed record AudioChunk(ReadOnlyMemory<byte> Data, string? MimeType);

/// <summary>
/// 语音合成（TTS）请求。
/// </summary>
public sealed record SpeechSynthesisRequest
{
    /// <summary>模型 ID。</summary>
    public required string Model { get; init; }

    /// <summary>待合成文本。</summary>
    public required string Text { get; init; }

    /// <summary>音色名。</summary>
    public string? Voice { get; init; }

    /// <summary>输出格式（如 <c>mp3</c> / <c>wav</c> / <c>opus</c>）。</summary>
    public string? Format { get; init; }

    /// <summary>语速倍率。</summary>
    public double? Speed { get; init; }
}

/// <summary>
/// 语音合成结果（非流式：完整音频字节）。
/// </summary>
/// <param name="Audio">完整音频字节。</param>
/// <param name="MimeType">音频 MIME 类型。</param>
/// <param name="Usage">token 用量；只进 <c>llm.after_complete</c> 载荷，不写会话预算（决议 1）。</param>
public sealed record SpeechSynthesisResult(ReadOnlyMemory<byte> Audio, string MimeType, TokenUsage? Usage);

/// <summary>
/// 语音转写（ASR）请求。
/// <para><b><see cref="Audio"/> 由调用方拥有，本契约的所有实现均不得 Dispose 它</b>（决议 17）。
/// 这是 .NET 惯例：接受 <see cref="Stream"/> 参数的方法不负责释放。</para>
/// </summary>
public sealed record TranscriptionRequest
{
    /// <summary>模型 ID。</summary>
    public required string Model { get; init; }

    /// <summary>
    /// 待转写的音频流。<b>由调用方拥有并负责关闭，实现只读不得 Dispose</b>（决议 17）。
    /// </summary>
    public required Stream Audio { get; init; }

    /// <summary>音频 MIME 类型（multipart 上传时的 content-type）。</summary>
    public string? MimeType { get; init; }

    /// <summary>文件名（multipart 上传的 filename）。</summary>
    public string? FileName { get; init; }

    /// <summary>语言提示（如 <c>zh</c> / <c>en</c>）。</summary>
    public string? Language { get; init; }
}

/// <summary>
/// 转写段落（带时间区间）。
/// </summary>
/// <param name="Start">起始时间。</param>
/// <param name="End">结束时间。</param>
/// <param name="Text">段落文本。</param>
public sealed record TranscriptSegment(TimeSpan Start, TimeSpan End, string Text);

/// <summary>
/// 转写结果。
/// </summary>
/// <param name="Text">全文。</param>
/// <param name="Segments">段落列表；服务端未返回细粒度时为 <c>null</c>。</param>
/// <param name="Usage">token 用量；只进 <c>llm.after_complete</c> 载荷，不写会话预算（决议 1）。</param>
public sealed record TranscriptionResult(string Text, IReadOnlyList<TranscriptSegment>? Segments, TokenUsage? Usage);
