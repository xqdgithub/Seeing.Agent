using Seeing.Agent.Abstractions.Background;

namespace Seeing.Agent.Core.Background;

/// <summary>
/// 活动执行侧上报端口（internal：只由 <see cref="BackgroundActivityManager"/> 在启动时创建并下发）。
/// <para>
/// 所有写入都回到 manager 的 CAS：进入终态之后的方法全部为 no-op。
/// </para>
/// </summary>
internal sealed class BackgroundActivityReporter : IBackgroundActivityReporter
{
    private readonly BackgroundActivityManager _manager;
    private readonly string _activityId;

    public BackgroundActivityReporter(BackgroundActivityManager manager, string activityId)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        ArgumentException.ThrowIfNullOrWhiteSpace(activityId);
        _activityId = activityId;
    }

    /// <inheritdoc />
    public void ReportStarted() => _manager.ReportStarted(_activityId);

    /// <inheritdoc />
    public void ReportProgress(BackgroundActivityProgress progress) => _manager.ReportProgress(_activityId, progress);

    /// <inheritdoc />
    public void Heartbeat() => _manager.Heartbeat(_activityId);

    /// <inheritdoc />
    public Task AppendOutputAsync(ReadOnlyMemory<byte> chunk, CancellationToken ct = default)
        => _manager.AppendOutputAsync(_activityId, chunk, ct);

    /// <inheritdoc />
    public Task CompleteAsync(BackgroundActivityResult result, CancellationToken ct = default)
        => _manager.CompleteAsync(_activityId, result, ct);

    /// <inheritdoc />
    public Task FailAsync(string errorCode, string errorMessage, CancellationToken ct = default)
        => _manager.FailAsync(_activityId, errorCode, errorMessage, ct);
}
