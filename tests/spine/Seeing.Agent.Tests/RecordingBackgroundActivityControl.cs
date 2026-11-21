using Seeing.Agent.Abstractions.Background;

namespace Seeing.Agent.Tests;

/// <summary>
/// 记录调用的后台活动系统面替身。
/// <para>
/// <c>SessionRuntime</c> 现在把 <see cref="IBackgroundActivityControl"/> 当必需依赖解析，
/// 所以用裸 <c>ServiceCollection</c> 构造运行时的用例都必须先注册一个实现。
/// 本替身记录「按执行取消」与「按会话取消」两类调用，其余成员保持不参与
/// （被调用即说明测试走错了路径）。
/// </para>
/// </summary>
internal sealed class RecordingBackgroundActivityControl : IBackgroundActivityControl
{
    public sealed record CancelCall(string ExecutionId, BackgroundActivityCancelReason Reason);

    public sealed record SessionCancelCall(string SessionId, BackgroundActivityCancelReason Reason);

    public List<CancelCall> Calls { get; } = new();

    /// <summary>按会话取消的调用记录（<c>StopAsync</c> 级联走这条路径）。</summary>
    public List<SessionCancelCall> SessionCalls { get; } = new();

    public Task<int> CancelByExecutionAsync(
        string executionId,
        BackgroundActivityCancelReason reason,
        CancellationToken ct = default)
    {
        lock (Calls)
            Calls.Add(new CancelCall(executionId, reason));

        return Task.FromResult(0);
    }

    public Task<BackgroundActivityHandle> StartAsSystemAsync(
        BackgroundActivityStartRequest request, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<BackgroundActivityHandle> TrackAsync(
        BackgroundActivityTrackRequest request, CancellationToken ct = default)
        => throw new NotSupportedException();

    public void RegisterLauncher(string moduleId, IBackgroundActivityLauncher launcher)
        => throw new NotSupportedException();

    public Task<int> UnregisterLauncherAsync(string kind, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<int> CancelBySessionAsync(
        string sessionId,
        BackgroundActivityCancelReason reason,
        CancellationToken ct = default)
    {
        lock (SessionCalls)
            SessionCalls.Add(new SessionCancelCall(sessionId, reason));

        return Task.FromResult(0);
    }

    public Task<int> CancelAllAsync(
        BackgroundActivityCancelReason reason,
        CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<int> ReconcileOrphanedActivitiesAsync(CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<int> PruneAsync(DateTimeOffset cutoff, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<bool> DetachAsync(string activityId, CancellationToken ct = default)
        => throw new NotSupportedException();
}
