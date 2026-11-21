using Seeing.Agent.Abstractions.Background;

namespace Seeing.Agent.Tests.Tools;

/// <summary>
/// 可控的后台活动 manager：记录启动/等待/取消/读输出，并按预设返回结果。
/// <para>
/// <c>bash</c> 前台与后台都经 manager 执行，测试因此不需要任何真实子进程。
/// </para>
/// </summary>
internal sealed class FakeBackgroundActivityManager : IBackgroundActivityManager
{
    public List<BackgroundActivityStartRequest> Requests { get; } = [];

    public List<string> RequesterSessionIds { get; } = [];

    public List<(string ActivityId, TimeSpan Timeout)> Waits { get; } = [];

    public List<(string ActivityId, BackgroundActivityCancelReason Reason, string RequesterSessionId)> Cancels { get; } = [];

    public List<(string ActivityId, long Offset, int MaxBytes)> OutputReads { get; } = [];

    public Func<int, BackgroundActivityStartRequest, Task<BackgroundActivityHandle>> StartHandler { get; set; }
        = (ordinal, _) => Task.FromResult(NewHandle($"act-{ordinal}"));

    /// <summary>默认：立即以 <c>Succeeded(exit 0)</c> 终态返回。</summary>
    public Func<string, BackgroundActivityWaitResult> WaitHandler { get; set; }
        = id => Completed(Snapshot(id, BackgroundActivityStatus.Succeeded, exitCode: 0));

    /// <summary>默认：空输出。</summary>
    public Func<string, long, int, BackgroundActivityOutputPage> OutputHandler { get; set; }
        = (_, offset, _) => new BackgroundActivityOutputPage(string.Empty, offset, EndOfStream: true, Truncated: false);

    public Task<BackgroundActivityHandle> StartAsync(
        BackgroundActivityStartRequest request,
        string requesterSessionId,
        CancellationToken ct = default)
    {
        Requests.Add(request);
        RequesterSessionIds.Add(requesterSessionId);
        return StartHandler(Requests.Count, request);
    }

    public BackgroundActivitySnapshot? Get(string activityId, string requesterSessionId) => null;

    public IReadOnlyList<BackgroundActivitySnapshot> List(BackgroundActivityQuery query, string requesterSessionId) => [];

    public Task<bool> CancelAsync(
        string activityId,
        BackgroundActivityCancelReason reason,
        string requesterSessionId,
        CancellationToken ct = default)
    {
        Cancels.Add((activityId, reason, requesterSessionId));
        return Task.FromResult(true);
    }

    public Task<BackgroundActivityWaitResult> WaitAsync(
        string activityId,
        TimeSpan timeout,
        string requesterSessionId,
        CancellationToken ct = default)
    {
        Waits.Add((activityId, timeout));
        return Task.FromResult(WaitHandler(activityId));
    }

    public Task<BackgroundActivityOutputPage> ReadOutputAsync(
        string activityId,
        long offset,
        int maxBytes,
        string requesterSessionId,
        CancellationToken ct = default)
    {
        OutputReads.Add((activityId, offset, maxBytes));
        return Task.FromResult(OutputHandler(activityId, offset, maxBytes));
    }

    public void MarkResultObserved(string activityId, string requesterSessionId) { }

    // ─── 构造 helper ─────────────────────────────────────────────────────────

    public static BackgroundActivityHandle NewHandle(string activityId)
        => new(activityId, new NoopReporter(), CancellationToken.None);

    public static BackgroundActivityWaitResult Completed(BackgroundActivitySnapshot snapshot)
        => new(BackgroundActivityWaitOutcome.Completed, snapshot);

    public static BackgroundActivityWaitResult WaitOutcome(
        string activityId, BackgroundActivityWaitOutcome outcome, BackgroundActivityStatus status)
        => new(outcome, Snapshot(activityId, status));

    public static BackgroundActivitySnapshot Snapshot(
        string id,
        BackgroundActivityStatus status,
        int? exitCode = null,
        BackgroundActivityCancelReason? cancelReason = null,
        string? errorCode = null,
        string? errorMessage = null)
    {
        var hasResult = status is BackgroundActivityStatus.Succeeded or BackgroundActivityStatus.Failed
            || exitCode.HasValue || errorCode is not null || errorMessage is not null;

        return new BackgroundActivitySnapshot
        {
            Id = id,
            Kind = "process",
            Title = "test",
            OwnerSessionId = "session",
            OwnerExecutionId = "exec",
            WorkspaceRoot = Path.GetTempPath(),
            HostInstanceId = "host",
            Status = status,
            CancelReason = cancelReason,
            Result = hasResult
                ? new BackgroundActivityResult(
                    BackgroundActivityResultKind.ProcessExit, null, null, exitCode, errorCode, errorMessage)
                : null
        };
    }

    /// <summary>单页输出：一次读完。</summary>
    public static Func<string, long, int, BackgroundActivityOutputPage> SinglePage(string content)
        => (_, offset, _) => new BackgroundActivityOutputPage(content, offset, EndOfStream: true, Truncated: false);

    private sealed class NoopReporter : IBackgroundActivityReporter
    {
        public void ReportStarted() { }
        public void ReportProgress(BackgroundActivityProgress progress) { }
        public void Heartbeat() { }
        public Task AppendOutputAsync(ReadOnlyMemory<byte> chunk, CancellationToken ct = default) => Task.CompletedTask;
        public Task CompleteAsync(BackgroundActivityResult result, CancellationToken ct = default) => Task.CompletedTask;
        public Task FailAsync(string errorCode, string errorMessage, CancellationToken ct = default) => Task.CompletedTask;
    }
}
