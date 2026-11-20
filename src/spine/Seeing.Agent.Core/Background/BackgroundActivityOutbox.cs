using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Background;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Agent.Configuration;
using Seeing.Agent.Paths;
using Seeing.Files;
using Seeing.Serialization;

namespace Seeing.Agent.Core.Background;

/// <summary>
/// manager / 工作区切换参与者使用的 outbox 写入口（internal：不属于公开契约）。
/// <para>
/// 公开的 <see cref="IBackgroundActivityOutbox"/> 只有「读侧」（<see cref="IBackgroundActivityOutbox.PumpOnceAsync"/>）；
/// 入队与清理是 Core 内部协作，不进能力包可见面。
/// </para>
/// </summary>
internal interface IBackgroundActivityNotificationQueue
{
    /// <summary>把一条完成通知写入 outbox（<c>digestId</c> / 宿主 / 时间由实现分配）。</summary>
    Task EnqueueAsync(BackgroundActivityNotification notification);

    /// <summary>
    /// 把某宿主在某活动根下的 pending 批次标 <c>Dropped</c>，返回受影响条数。
    /// <para>
    /// 工作区切换必须传入<b>旧</b>项目根：参与者执行时提供者尚未切换，但显式传根可以避免
    /// 任何「读到了新根」的时序假设。
    /// </para>
    /// </summary>
    Task<int> MarkDroppedAsync(string hostInstanceId, string? projectRoot = null, string? reason = null);
}

/// <summary>
/// <see cref="IBackgroundActivityOutbox"/> 的唯一实现：在
/// <c>{活动根}/outbox/{digestId}.json</c> 上做「先落盘、后投递」的完成通知队列（spec §10）。
/// <para>
/// <b>合并</b>：同一归属会话在合并窗口（默认 300ms，<see cref="TimeProvider"/> 驱动）内的多条
/// 完成通知合成一个 digest；合并结果先原子落盘成**新的** <c>digestId</c>，再投递（spec §10.2）。
/// 投递成功后重试只重放同一 digest，不会重复投递。
/// </para>
/// <para>
/// <b>多实例隔离</b>：只泵取 <see cref="BackgroundActivityHostInstance.Current"/> 自己的 pending；
/// 别的存活实例的批次完全不动（只在工作区切换时由参与者按旧根清理）。
/// </para>
/// <para>
/// <b>已观察不投递</b>：批次里的活动若都已 <c>ResultObservedAt != null</c>，直接标
/// <c>Delivered</c>（spec §10.6）；部分已观察时只投递剩余部分。
/// </para>
/// <para>
/// <b>不做</b>：跨重启补发、跨实例接管。进程内 at-least-once 是明确范围（spec §10.4）。
/// </para>
/// </summary>
internal sealed class BackgroundActivityOutbox : IBackgroundActivityOutbox, IBackgroundActivityNotificationQueue
{
    /// <summary>合并窗口默认值（spec §15）。</summary>
    internal static readonly TimeSpan DefaultMergeWindow = TimeSpan.FromMilliseconds(300);

    /// <summary>单批次最大投递失败次数：达到即 <c>Dropped</c>（spec §15「outbox 重试 5 次」）。</summary>
    internal const int MaxDeliveryAttempts = 5;

    /// <summary>outbox 子目录名。</summary>
    internal const string DirectoryName = BackgroundActivityPaths.OutboxDirectoryName;

    /// <summary>批次文件后缀。</summary>
    internal const string FileSuffix = ".json";

    /// <summary>唤醒消息的来源标签（spec §10.3）。</summary>
    internal const string SourceTag = "background.activity";

    /// <summary>幂等键前缀：同一 digest 重试复用同一个键。</summary>
    internal const string IdempotencyKeyPrefix = "activity-digest:";

    private const string DigestIdPrefix = "dgt_";
    private const int DigestIdHexLength = 32;
    private const int MaxTitleLength = 120;

    private static readonly JsonSerializerOptions s_json = SeeingJson.Create(
        indented: true,
        static options => options.Converters.Add(new JsonStringEnumConverter()));

    private static readonly UTF8Encoding s_utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly IWorkspaceProvider _workspace;
    private readonly IBackgroundActivityStore _store;
    private readonly IBackgroundActivityNotifier _notifier;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<BackgroundActivityOutbox> _logger;
    private readonly TimeSpan _mergeWindow;
    private readonly string _hostInstanceId;

    /// <summary>泵取互斥闸：同一实例内不并发投递（合并批次也不允许被两次读取）。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>本实例已知的待投递条数（信息性gauge；不做跨实例精确统计）。</summary>
    private int _pendingCount;

    public BackgroundActivityOutbox(
        IWorkspaceProvider workspace,
        IBackgroundActivityStore store,
        IBackgroundActivityNotifier notifier,
        TimeProvider timeProvider,
        ILogger<BackgroundActivityOutbox> logger,
        TimeSpan? mergeWindow = null,
        string? hostInstanceId = null)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _mergeWindow = mergeWindow is { } window && window > TimeSpan.Zero ? window : DefaultMergeWindow;
        _hostInstanceId = string.IsNullOrEmpty(hostInstanceId)
            ? BackgroundActivityHostInstance.Current
            : hostInstanceId!;
    }

    /// <inheritdoc />
    public int PendingCount => Volatile.Read(ref _pendingCount);

    // ─── 入队（internal 协作） ────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task EnqueueAsync(BackgroundActivityNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentException.ThrowIfNullOrWhiteSpace(notification.OwnerSessionId);

        if (notification.ActivityIds is null || notification.ActivityIds.Count == 0)
            return;

        // 批次身份一律由 outbox 分配：manager 只声明「哪个会话的哪些活动完成了」。
        var persisted = notification with
        {
            DigestId = NewDigestId(),
            HostInstanceId = _hostInstanceId,
            ActivityIds = notification.ActivityIds
                .Where(static id => !string.IsNullOrEmpty(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            Status = BackgroundActivityOutboxStatus.Pending,
            Attempts = 0,
            LastError = null,
            CreatedAt = _timeProvider.GetLocalNow()
        };

        if (persisted.ActivityIds.Count == 0)
            return;

        await WriteAsync(persisted).ConfigureAwait(false);
        Interlocked.Increment(ref _pendingCount);
    }

    /// <inheritdoc />
    public async Task<int> MarkDroppedAsync(
        string hostInstanceId,
        string? projectRoot = null,
        string? reason = null)
    {
        if (string.IsNullOrEmpty(hostInstanceId))
            return 0;

        // 没有旧根（首次选定工作区）时没有可清理的批次。
        if (projectRoot is not null && string.IsNullOrWhiteSpace(projectRoot))
            return 0;

        var directory = projectRoot is null
            ? OutboxDirectory()
            : Path.Combine(BackgroundActivityPaths.FromProjectRoot(projectRoot), DirectoryName);

        // 与泵取共用互斥闸：切换期间不能再有批次被投递出去。
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var all = await LoadAllAsync(directory).ConfigureAwait(false);
            var dropped = 0;
            foreach (var notification in all)
            {
                if (notification.Status != BackgroundActivityOutboxStatus.Pending)
                    continue;

                if (!string.Equals(notification.HostInstanceId, hostInstanceId, StringComparison.Ordinal))
                    continue;

                await WriteAsync(notification with
                {
                    Status = BackgroundActivityOutboxStatus.Dropped,
                    LastError = reason ?? "dropped"
                }, directory).ConfigureAwait(false);

                dropped++;
            }

            if (dropped > 0 && string.Equals(hostInstanceId, _hostInstanceId, StringComparison.Ordinal))
                Interlocked.Add(ref _pendingCount, -dropped);

            return dropped;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ─── 泵取 ─────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<int> PumpOnceAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _timeProvider.GetLocalNow();
            var activityRoot = BackgroundActivityPaths.ResolveActivityRoot(_workspace);
            var all = await LoadAllAsync(OutboxDirectory(activityRoot)).ConfigureAwait(false);

            var mine = all
                .Where(n => n.Status == BackgroundActivityOutboxStatus.Pending
                            && string.Equals(n.HostInstanceId, _hostInstanceId, StringComparison.Ordinal))
                .OrderBy(static n => n.CreatedAt)
                .ToList();

            if (mine.Count == 0)
            {
                Volatile.Write(ref _pendingCount, 0);
                return 0;
            }

            // 合并窗口：窗口内的新条目先留在盘上，等下一个泵周期一起合并成一条。
            var eligible = mine
                .Where(n => now - n.CreatedAt >= _mergeWindow)
                .ToList();

            if (eligible.Count == 0)
            {
                Volatile.Write(ref _pendingCount, mine.Count);
                return 0;
            }

            // 一次批量取齐所有候选活动的快照：已观察过滤与展示摘要共用同一份数据。
            var candidateIds = eligible
                .SelectMany(static n => n.ActivityIds)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var snapshots = await _store.LoadManyAsync(candidateIds).ConfigureAwait(false);
            var byId = new Dictionary<string, BackgroundActivitySnapshot>(StringComparer.Ordinal);
            foreach (var snapshot in snapshots)
                byId[snapshot.Id] = snapshot;

            var remaining = mine.Count;
            var ready = new List<DeliveryCandidate>();
            foreach (var notification in eligible)
            {
                var deliverable = new List<string>();
                var anyObserved = false;

                foreach (var activityId in notification.ActivityIds.Distinct(StringComparer.Ordinal))
                {
                    if (!byId.TryGetValue(activityId, out var snapshot))
                        continue;

                    if (snapshot.ResultObservedAt is not null)
                    {
                        anyObserved = true;
                        continue;
                    }

                    deliverable.Add(activityId);
                }

                if (deliverable.Count == 0)
                {
                    // 全部已观察（或活动记录已不可读）：不再唤醒，直接终态（spec §10.6）。
                    await WriteAsync(notification with
                    {
                        Status = BackgroundActivityOutboxStatus.Delivered,
                        LastError = anyObserved ? "result_observed" : "activity_missing"
                    }).ConfigureAwait(false);

                    remaining--;
                    continue;
                }

                ready.Add(new DeliveryCandidate(notification, deliverable));
            }

            var deliveries = new List<BackgroundActivityNotification>();

            // 首次投递（Attempts == 0）：按归属会话合并成一条 digest。
            foreach (var group in ready
                         .Where(static r => r.Notification.Attempts == 0)
                         .GroupBy(static r => r.Notification.OwnerSessionId, StringComparer.Ordinal))
            {
                var digest = new BackgroundActivityNotification
                {
                    DigestId = NewDigestId(),
                    HostInstanceId = _hostInstanceId,
                    OwnerSessionId = group.Key,
                    ActivityIds = group
                        .SelectMany(static r => r.Ids)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray(),
                    Status = BackgroundActivityOutboxStatus.Pending,
                    CreatedAt = now
                };

                // 先落盘批次，再投递（spec §10.2）：重试只重放这一份 digest。
                await WriteAsync(digest).ConfigureAwait(false);
                remaining++;

                foreach (var source in group)
                {
                    // 源条目的内容已被 digest 吞掉：标记为终态，避免下一轮再被合并成第二个 digest。
                    await WriteAsync(source.Notification with
                    {
                        Status = BackgroundActivityOutboxStatus.Delivered,
                        LastError = "merged_into:" + digest.DigestId
                    }).ConfigureAwait(false);

                    remaining--;
                }

                deliveries.Add(digest);
            }

            // 重试：保留原 digestId（幂等键不变），不参与重新合并。
            foreach (var retry in ready.Where(static r => r.Notification.Attempts > 0))
            {
                deliveries.Add(retry.Notification with { ActivityIds = retry.Ids.ToArray() });
            }

            var processed = 0;
            foreach (var digest in deliveries)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    await _notifier
                        .DeliverAsync(BuildMessage(digest, byId, activityRoot), ct)
                        .ConfigureAwait(false);

                    await WriteAsync(digest with
                    {
                        Status = BackgroundActivityOutboxStatus.Delivered,
                        LastError = null
                    }).ConfigureAwait(false);

                    remaining--;
                    processed++;
                }
                catch (BackgroundActivityTargetSessionMissingException ex)
                {
                    // 目标会话已被删除：再重试也不会成功，直接丢弃（spec §10.1）。
                    await WriteAsync(digest with
                    {
                        Status = BackgroundActivityOutboxStatus.Dropped,
                        LastError = "session_missing"
                    }).ConfigureAwait(false);

                    remaining--;
                    processed++;

                    _logger.LogInformation(
                        "后台活动完成通知丢弃（目标会话不存在） DigestId={DigestId} SessionId={SessionId}",
                        digest.DigestId,
                        ex.TargetSessionId);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // 宿主停止：批次留在盘上，进程内不再补发（跨重启不承诺，spec §10.4）。
                    throw;
                }
                catch (Exception ex)
                {
                    var attempts = digest.Attempts + 1;
                    var dropped = attempts >= MaxDeliveryAttempts;

                    await WriteAsync(digest with
                    {
                        Status = dropped
                            ? BackgroundActivityOutboxStatus.Dropped
                            : BackgroundActivityOutboxStatus.Pending,
                        Attempts = attempts,
                        LastError = ex.Message
                    }).ConfigureAwait(false);

                    if (dropped)
                        remaining--;

                    processed++;

                    if (dropped)
                    {
                        _logger.LogWarning(
                            ex,
                            "后台活动完成通知投递失败已达上限，丢弃 DigestId={DigestId} SessionId={SessionId} Attempts={Attempts}",
                            digest.DigestId,
                            digest.OwnerSessionId,
                            attempts);
                    }
                    else
                    {
                        _logger.LogWarning(
                            ex,
                            "后台活动完成通知投递失败，下个窗口重试 DigestId={DigestId} SessionId={SessionId} Attempts={Attempts}",
                            digest.DigestId,
                            digest.OwnerSessionId,
                            attempts);
                    }
                }
            }

            Volatile.Write(ref _pendingCount, Math.Max(0, remaining));
            return processed;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ─── 消息构造（摘要，不含输出正文） ───────────────────────────────────────

    /// <summary>
    /// 构造唤醒消息：只会携带标题 / kind / 状态 / 输出**大小**，绝不内联输出正文（spec §19 红线 3）。
    /// </summary>
    internal InboxMessage BuildMessage(
        BackgroundActivityNotification digest,
        IReadOnlyDictionary<string, BackgroundActivitySnapshot> snapshots,
        string activityRoot)
        => new()
        {
            TargetSessionId = digest.OwnerSessionId,
            Sender = new InboxSender { Kind = InboxSenderKind.System, Id = "background" },
            Body = BuildBody(digest, snapshots, activityRoot),
            Delivery = InboxDelivery.Wake,
            Presentation = InboxPresentation.System,
            SourceTag = SourceTag,
            IdempotencyKey = IdempotencyKeyPrefix + digest.DigestId
        };

    private static string BuildBody(
        BackgroundActivityNotification digest,
        IReadOnlyDictionary<string, BackgroundActivitySnapshot> snapshots,
        string activityRoot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("【后台活动完成】");

        foreach (var activityId in digest.ActivityIds)
        {
            if (!snapshots.TryGetValue(activityId, out var snapshot))
            {
                builder.AppendLine($"- {activityId}（记录已不可读）");
                continue;
            }

            var facts = new List<string>(4)
            {
                $"kind={snapshot.Kind}",
                StatusText(snapshot.Status)
            };

            if (snapshot.Result?.ExitCode is { } exitCode)
                facts.Add($"exit {exitCode}");

            facts.Add($"输出 {FormatSize(ReadOutputSize(activityRoot, activityId))}");

            builder.AppendLine($"- {Flatten(snapshot.Title)}（{string.Join("，", facts)}）");
        }

        builder.Append("用 `activity_output` 查看详情；`activity_status` 查看状态。");
        return builder.ToString();
    }

    private static string StatusText(BackgroundActivityStatus status) => status switch
    {
        BackgroundActivityStatus.Succeeded => "成功",
        BackgroundActivityStatus.Failed => "失败",
        BackgroundActivityStatus.Cancelled => "已取消",
        BackgroundActivityStatus.Interrupted => "已中断",
        BackgroundActivityStatus.Running => "运行中",
        _ => "等待中"
    };

    /// <summary>标题来自 launcher（可能含换行）：压成单行并限长，避免一条标题撑爆摘要。</summary>
    private static string Flatten(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return "(未命名活动)";

        var flat = title.ReplaceLineEndings(" ").Trim();
        return flat.Length <= MaxTitleLength ? flat : flat[..MaxTitleLength] + "…";
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";

        if (bytes < 1024 * 1024)
            return $"{bytes / 1024.0:0.#} KB";

        return $"{bytes / (1024.0 * 1024.0):0.#} MB";
    }

    /// <summary>只读文件长度（不读内容）：输出正文永不进入通知。</summary>
    private static long ReadOutputSize(string activityRoot, string activityId)
    {
        try
        {
            var path = Path.Combine(activityRoot, activityId + BackgroundActivityPaths.OutputFileSuffix);
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    // ─── 存储 ─────────────────────────────────────────────────────────────────

    private string OutboxDirectory() => OutboxDirectory(BackgroundActivityPaths.ResolveActivityRoot(_workspace));

    private static string OutboxDirectory(string activityRoot) => Path.Combine(activityRoot, DirectoryName);

    private async Task<List<BackgroundActivityNotification>> LoadAllAsync(string directory)
    {
        if (!Directory.Exists(directory))
            return new List<BackgroundActivityNotification>();

        string[] files;
        try
        {
            files = Directory.GetFiles(directory, "*" + FileSuffix, SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "后台活动 outbox 枚举失败，按空批次处理 Directory={Directory}", directory);
            return new List<BackgroundActivityNotification>();
        }

        var result = new List<BackgroundActivityNotification>(files.Length);
        foreach (var path in files)
        {
            var digestId = Path.GetFileNameWithoutExtension(path);
            if (!IsValidDigestId(digestId))
                continue;

            var notification = await ReadAsync(path).ConfigureAwait(false);
            if (notification is null)
                continue;

            // 文件名与内部 id 不一致说明被改动过：按不可信跳过，避免标记到别的批次。
            if (!string.Equals(notification.DigestId, digestId, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "后台活动 outbox 批次 id 与文件名不一致，跳过 Path={Path} DigestId={DigestId}",
                    path,
                    notification.DigestId);
                continue;
            }

            result.Add(notification);
        }

        return result;
    }

    private async Task<BackgroundActivityNotification?> ReadAsync(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                useAsync: true);

            return await JsonSerializer
                .DeserializeAsync<BackgroundActivityNotification>(stream, s_json)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            _logger.LogWarning(ex, "后台活动 outbox 批次读取失败，跳过 Path={Path}", path);
            return null;
        }
    }

    /// <summary>原子写一条批次；<paramref name="directory"/> 缺省时用当前活动根（旧根清理需显式传）。</summary>
    private async Task WriteAsync(BackgroundActivityNotification notification, string? directory = null)
    {
        var path = ResolveNotificationPath(notification.DigestId, directory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // 终态必须落盘：与快照一致，不受调用方取消影响。
        var gate = AtomicFileWrite.GetLock(path);
        await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var json = JsonSerializer.Serialize(notification, s_json);
            var tempPath = AtomicFileWrite.CreateTempPath(path);
            try
            {
                await File.WriteAllTextAsync(tempPath, json, s_utf8NoBom, CancellationToken.None)
                    .ConfigureAwait(false);
                await AtomicFileWrite
                    .MoveWithRetryAsync(tempPath, path, notification.DigestId, _logger, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                AtomicFileWrite.TryDelete(tempPath);
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private string ResolveNotificationPath(string digestId, string? directory)
    {
        if (!IsValidDigestId(digestId))
            throw new ArgumentException($"非法的后台活动通知批次 id：'{digestId}'。", nameof(digestId));

        var root = directory ?? OutboxDirectory();
        var path = Path.Combine(root, digestId + FileSuffix);

        // 纵深防御：id 已由正则约束，这里再确认拼出的路径确实落在 outbox 内（符号链接等）。
        if (!PathBoundary.IsStrictlyWithin(path, root))
            throw new UnauthorizedAccessException($"后台活动通知批次路径越界：'{path}'（根：'{root}'）。");

        return path;
    }

    // ─── 静态辅助 ─────────────────────────────────────────────────────────────

    private static string NewDigestId() => DigestIdPrefix + Guid.NewGuid().ToString("N");

    private static bool IsValidDigestId(string? digestId)
    {
        if (digestId is null || digestId.Length != DigestIdPrefix.Length + DigestIdHexLength)
            return false;

        if (!digestId.StartsWith(DigestIdPrefix, StringComparison.Ordinal))
            return false;

        for (var i = DigestIdPrefix.Length; i < digestId.Length; i++)
        {
            var c = digestId[i];
            var isHex = c is >= '0' and <= '9' or >= 'a' and <= 'f';
            if (!isHex)
                return false;
        }

        return true;
    }

    /// <summary>待投递候选：一条通知 + 本轮真正要投递的活动 id（已剔除被观察的部分）。</summary>
    private readonly record struct DeliveryCandidate(
        BackgroundActivityNotification Notification,
        IReadOnlyList<string> Ids);
}
