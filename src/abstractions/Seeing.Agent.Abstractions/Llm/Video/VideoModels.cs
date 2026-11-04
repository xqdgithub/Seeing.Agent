namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// 视频生成请求（作业型协议，本次只出契约，spec §7.11）。
/// </summary>
public sealed record VideoGenerationRequest
{
    /// <summary>模型 ID。</summary>
    public required string Model { get; init; }

    /// <summary>提示词。</summary>
    public required string Prompt { get; init; }

    /// <summary>首帧参考图 URL（图生视频场景）。</summary>
    public string? ImageUrl { get; init; }

    /// <summary>目标时长。</summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>分辨率（如 <c>1280x720</c>）。</summary>
    public string? Size { get; init; }
}

/// <summary>
/// 媒体作业句柄：<see cref="JobId"/> 与 <see cref="ProviderId"/> 一起才唯一
/// （不同 Provider 的 JobId 可能重号）。
/// </summary>
/// <param name="JobId">服务端作业 ID。</param>
/// <param name="ProviderId">所属 Provider ID。</param>
public sealed record MediaJobHandle(string JobId, string ProviderId);

/// <summary>
/// 媒体作业状态机。轮询以进入终态为止。
/// </summary>
public enum MediaJobState
{
    /// <summary>已提交、排队中。</summary>
    Queued,

    /// <summary>执行中。</summary>
    Running,

    /// <summary>成功（终态）。</summary>
    Succeeded,

    /// <summary>失败（终态）。</summary>
    Failed,

    /// <summary>已取消（终态）。</summary>
    Cancelled
}

/// <summary>
/// 媒体作业状态快照。
/// </summary>
/// <param name="State">当前状态。</param>
/// <param name="Url">产物 URL（仅 Succeeded 有值）。</param>
/// <param name="Error">失败原因（仅 Failed 有值）。</param>
/// <param name="Progress">进度 [0,1]（服务端不回显时为 null）。</param>
public sealed record MediaJobStatus(MediaJobState State, string? Url, string? Error, double? Progress);
