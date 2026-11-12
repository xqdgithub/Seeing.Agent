using Seeing.Agent.Reflection.Abstractions;

namespace Seeing.Agent.Reflection.Core;
/// <summary>
/// <see cref="IReflectionStatus"/> 的组装实现：把四个内部服务的事实读成一份快照。
/// <para>
/// 这里只做聚合，不缓存：设置卡每次刷新都想要当下事实，
/// 缓存会让「刚开启却显示未生效」这种误导更持久。
/// </para>
/// </summary>
public sealed class ReflectionStatus : IReflectionStatus
{
    private readonly ReflectionModuleActivity _activity;
    private readonly IExecutionUsageLedger _ledger;
    private readonly IReflectionQueue _queue;
    private readonly ReflectionRunningTracker _running;
    private readonly ReflectionStorageRoot _storage;

    public ReflectionStatus(
        ReflectionModuleActivity activity,
        IExecutionUsageLedger ledger,
        IReflectionQueue queue,
        ReflectionRunningTracker running,
        ReflectionStorageRoot storage)
    {
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _running = running ?? throw new ArgumentNullException(nameof(running));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    }

    /// <inheritdoc />
    public bool IsModuleActive => _activity.IsActive;

    /// <inheritdoc />
    /// <remarks>每次现算：切换工作区后必须显示新目录，否则用户按旧路径去翻找不到记录。</remarks>
    public string StorageDirectory => _storage.Path;

    /// <inheritdoc />
    public long UsedTodayTokens => _ledger.UsedToday();

    /// <inheritdoc />
    public int PendingCount => _queue.PendingCount;

    /// <inheritdoc />
    public IReadOnlyList<ReflectionRequest> Queued => _queue.Snapshot();

    /// <inheritdoc />
    public ReflectionRequest? Running => _running.Current;
}
