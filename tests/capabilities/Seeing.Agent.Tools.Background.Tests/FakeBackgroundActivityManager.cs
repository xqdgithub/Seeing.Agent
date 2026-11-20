using Seeing.Agent.Abstractions.Background;

namespace Seeing.Agent.Tools.Background.Tests;

/// <summary>
/// 会话面 manager 的内存假实现：按归属会话过滤，行为与真实 manager 的越权语义一致。
/// </summary>
internal sealed class FakeBackgroundActivityManager : IBackgroundActivityManager
{
    private readonly Dictionary<string, BackgroundActivitySnapshot> _activities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _outputs = new(StringComparer.Ordinal);

    public string? LastRequesterSessionId { get; private set; }

    public long? LastOffset { get; private set; }

    public int? LastMaxBytes { get; private set; }

    public TimeSpan? LastTimeout { get; private set; }

    public BackgroundActivityWaitOutcome WaitOutcome { get; set; } = BackgroundActivityWaitOutcome.Completed;

    public List<string> ObservedActivityIds { get; } = [];

    public void Add(BackgroundActivitySnapshot activity, string output = "")
    {
        _activities[activity.Id] = activity;
        _outputs[activity.Id] = output;
    }

    public BackgroundActivitySnapshot? Find(string activityId) =>
        _activities.GetValueOrDefault(activityId);

    public static BackgroundActivitySnapshot Snapshot(
        string id,
        string ownerSessionId,
        BackgroundActivityStatus status = BackgroundActivityStatus.Running,
        string kind = "process",
        string title = "build",
        BackgroundActivityVisibility visibility = BackgroundActivityVisibility.Session) =>
        new()
        {
            Id = id,
            Kind = kind,
            Title = title,
            Origin = BackgroundActivityOrigin.ToolBackground,
            WorkspaceRoot = @"C:\workspace",
            HostInstanceId = "host-1",
            OwnerSessionId = ownerSessionId,
            OwnerExecutionId = "exec-1",
            Status = status,
            Capabilities = BackgroundActivityCapabilities.CanCancel
                | BackgroundActivityCapabilities.CanWait
                | BackgroundActivityCapabilities.CanStreamOutput,
            Visibility = visibility,
            OutputLimitBytes = 5 * 1024 * 1024,
        };

    public Task<BackgroundActivityHandle> StartAsync(
        BackgroundActivityStartRequest request,
        string requesterSessionId,
        CancellationToken ct = default) =>
        throw new NotSupportedException("activity_* 工具不启动活动。");

    public BackgroundActivitySnapshot? Get(string activityId, string requesterSessionId)
    {
        LastRequesterSessionId = requesterSessionId;

        if (!_activities.TryGetValue(activityId, out var activity))
            return null;

        var readable = string.Equals(activity.OwnerSessionId, requesterSessionId, StringComparison.Ordinal)
            || activity.Visibility is BackgroundActivityVisibility.Project or BackgroundActivityVisibility.System;

        return readable ? activity : null;
    }

    public IReadOnlyList<BackgroundActivitySnapshot> List(
        BackgroundActivityQuery query,
        string requesterSessionId)
    {
        LastRequesterSessionId = requesterSessionId;

        return _activities.Values
            .Where(a => string.Equals(a.OwnerSessionId, requesterSessionId, StringComparison.Ordinal))
            .Where(a => query.Status is not { } status || a.Status == status)
            .Where(a => string.IsNullOrEmpty(query.Kind) || string.Equals(a.Kind, query.Kind, StringComparison.Ordinal))
            .Where(a => query.IncludeCompleted || !IsTerminal(a.Status))
            .OrderBy(a => a.CreatedAt)
            .ToList();
    }

    public Task<bool> CancelAsync(
        string activityId,
        BackgroundActivityCancelReason reason,
        string requesterSessionId,
        CancellationToken ct = default)
    {
        LastRequesterSessionId = requesterSessionId;

        if (!_activities.TryGetValue(activityId, out var activity)
            || !string.Equals(activity.OwnerSessionId, requesterSessionId, StringComparison.Ordinal)
            || IsTerminal(activity.Status))
        {
            return Task.FromResult(false);
        }

        _activities[activityId] = activity with
        {
            Status = BackgroundActivityStatus.Cancelled,
            CancelReason = reason,
            Seq = activity.Seq + 1,
        };
        return Task.FromResult(true);
    }

    public Task<BackgroundActivityWaitResult> WaitAsync(
        string activityId,
        TimeSpan timeout,
        string requesterSessionId,
        CancellationToken ct = default)
    {
        LastRequesterSessionId = requesterSessionId;
        LastTimeout = timeout;

        var activity = _activities.GetValueOrDefault(activityId);
        var owned = activity is not null
            && string.Equals(activity.OwnerSessionId, requesterSessionId, StringComparison.Ordinal);

        if (ct.IsCancellationRequested || WaitOutcome == BackgroundActivityWaitOutcome.CallerCancelled)
            return Task.FromResult(new BackgroundActivityWaitResult(
                BackgroundActivityWaitOutcome.CallerCancelled, owned ? activity : null));

        if (WaitOutcome == BackgroundActivityWaitOutcome.TimedOut || !owned)
            return Task.FromResult(new BackgroundActivityWaitResult(
                BackgroundActivityWaitOutcome.TimedOut, owned ? activity : null));

        return Task.FromResult(new BackgroundActivityWaitResult(
            BackgroundActivityWaitOutcome.Completed, activity));
    }

    public Task<BackgroundActivityOutputPage> ReadOutputAsync(
        string activityId,
        long offset,
        int maxBytes,
        string requesterSessionId,
        CancellationToken ct = default)
    {
        LastRequesterSessionId = requesterSessionId;
        LastOffset = offset;
        LastMaxBytes = maxBytes;

        var activity = _activities.GetValueOrDefault(activityId);
        var owned = activity is not null
            && string.Equals(activity.OwnerSessionId, requesterSessionId, StringComparison.Ordinal);

        if (!owned)
        {
            return Task.FromResult(new BackgroundActivityOutputPage(
                string.Empty, offset, EndOfStream: true, Truncated: false));
        }

        var output = _outputs.GetValueOrDefault(activityId, string.Empty);
        if (offset >= output.Length)
        {
            return Task.FromResult(new BackgroundActivityOutputPage(
                string.Empty, offset, EndOfStream: true, Truncated: false));
        }

        var take = Math.Min(maxBytes, output.Length - (int)offset);
        var content = output.Substring((int)offset, take);
        var nextOffset = offset + take;

        return Task.FromResult(new BackgroundActivityOutputPage(
            content, nextOffset, nextOffset >= output.Length, Truncated: false));
    }

    public void MarkResultObserved(string activityId, string requesterSessionId)
    {
        if (!_activities.TryGetValue(activityId, out var activity)
            || !string.Equals(activity.OwnerSessionId, requesterSessionId, StringComparison.Ordinal))
        {
            return;
        }

        ObservedActivityIds.Add(activityId);
    }

    private static bool IsTerminal(BackgroundActivityStatus status) =>
        status is BackgroundActivityStatus.Succeeded
            or BackgroundActivityStatus.Failed
            or BackgroundActivityStatus.Cancelled
            or BackgroundActivityStatus.Interrupted;
}
