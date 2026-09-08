namespace Seeing.Agent.Abstractions.Scheduling;

/// <summary>调度任务结果投递抽象（Scheduler / Gateway 等实现）。</summary>
public interface IScheduledJobDispatcher
{
    /// <summary>投递一次调度结果。</summary>
    Task<DispatchResult> DispatchAsync(DispatchRequest request, CancellationToken ct = default);
}

/// <summary>投递请求。</summary>
public sealed class DispatchRequest
{
    public required string Source { get; init; }
    public required string TaskType { get; init; }
    public required string Content { get; init; }
    public string? UserInput { get; init; }
    public string? Channel { get; init; }
    public string? UserId { get; init; }
    public string? SessionId { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>投递结果。</summary>
public sealed class DispatchResult
{
    public bool Success { get; init; } = true;
    public string? Error { get; init; }

    public static DispatchResult Ok() => new();
    public static DispatchResult Fail(string error) => new() { Success = false, Error = error };
}
