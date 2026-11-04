namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// 视频生成端口（<see cref="LlmCapabilityKind.VideoGeneration"/>）。
/// <para>视频是作业型协议：提交 → 轮询 → 取结果，因此端口以任务句柄为中心。
/// 本次只出契约，无可跑实现（spec §7.11）。</para>
/// </summary>
public interface IVideoGenerationClient : ILlmCapabilityClient
{
    /// <summary>提交生成作业，返回可轮询的任务句柄。</summary>
    Task<MediaJobHandle> SubmitAsync(
        VideoGenerationRequest request,
        LlmCallContext? call = null,
        CancellationToken ct = default);

    /// <summary>查询作业状态。</summary>
    Task<MediaJobStatus> GetStatusAsync(
        MediaJobHandle handle,
        LlmCallContext? call = null,
        CancellationToken ct = default);

    /// <summary>取消作业。</summary>
    Task CancelAsync(
        MediaJobHandle handle,
        LlmCallContext? call = null,
        CancellationToken ct = default);

    /// <summary>按固定间隔轮询作业，直到终态（Succeeded/Failed/Cancelled）或被取消。</summary>
    IAsyncEnumerable<MediaJobStatus> PollAsync(
        MediaJobHandle handle,
        TimeSpan interval,
        LlmCallContext? call = null,
        CancellationToken ct = default);
}
