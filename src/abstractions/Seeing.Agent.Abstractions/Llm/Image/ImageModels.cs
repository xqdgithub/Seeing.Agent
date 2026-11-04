namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// 图像生成请求（本次只出契约，spec §7.11）。
/// </summary>
public sealed record ImageGenerationRequest
{
    /// <summary>模型 ID。</summary>
    public required string Model { get; init; }

    /// <summary>提示词。</summary>
    public required string Prompt { get; init; }

    /// <summary>尺寸（如 <c>1024x1024</c>）。</summary>
    public string? Size { get; init; }

    /// <summary>生成张数。</summary>
    public int? Count { get; init; }

    /// <summary>负向提示词。</summary>
    public string? NegativePrompt { get; init; }
}

/// <summary>
/// 单张生成结果：远端 URL 或内联字节二选一。
/// </summary>
/// <param name="Url">远端可访问 URL（与 <paramref name="Data"/> 二选一）。</param>
/// <param name="Data">内联图像字节。</param>
/// <param name="MimeType">图像 MIME 类型。</param>
/// <param name="RevisedPrompt">服务端改写后的提示词（若有）。</param>
public sealed record GeneratedImage(string? Url, ReadOnlyMemory<byte>? Data, string? MimeType, string? RevisedPrompt);

/// <summary>
/// 图像生成结果。
/// </summary>
/// <param name="Images">生成的图像集合。</param>
/// <param name="Usage">token 用量；只进 <c>llm.after_complete</c> 载荷，不写会话预算（决议 1）。</param>
public sealed record ImageGenerationResult(IReadOnlyList<GeneratedImage> Images, TokenUsage? Usage);
