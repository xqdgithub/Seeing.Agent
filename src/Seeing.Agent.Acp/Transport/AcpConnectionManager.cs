using System.Collections.Concurrent;
using Acp.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Acp.Client;
using Seeing.Agent.Acp.Session;
using Seeing.Agent.Configuration;

namespace Seeing.Agent.Acp.Transport;

/// <summary>
/// 管理 ACP 子进程租约（passthrough:{backend}:{sessionId} / tool:{backend}:{taskId}）。
/// 同一租约在 <see cref="SeeingAgentOptions.Acp.IdleTimeout"/> 内可复用，超时后由后台任务回收。
/// 支持宽限期模式：<see cref="SeeingAgentOptions.Acp.SessionGracePeriod"/> 内可复用进程和 ACP Session。
/// </summary>
public sealed class AcpConnectionManager : IAsyncDisposable
{
    private sealed class LeaseEntry
    {
        public required AcpConnectionLease Lease { get; init; }

        public DateTime LastUsedUtc { get; set; } = DateTime.Now;

        public int ActiveUsers;

        /// <summary>宽限期取消令牌</summary>
        public CancellationTokenSource? GracePeriodCts;

        /// <summary>关联的 Seeing Session ID（用于清除缓存）</summary>
        public string? SeeingSessionId;
    }

    private readonly SeeingAcpClientFactory _clientFactory;
    private readonly AcpSessionStore _sessionStore;
    private readonly IOptions<SeeingAgentOptions> _options;
    private readonly ILogger<AcpConnectionManager> _logger;
    private readonly ConcurrentDictionary<string, LeaseEntry> _leases = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _promptGates = new();

    public AcpConnectionManager(
        SeeingAcpClientFactory clientFactory,
        AcpSessionStore sessionStore,
        IOptions<SeeingAgentOptions> options,
        ILogger<AcpConnectionManager> logger)
    {
        _clientFactory = clientFactory;
        _sessionStore = sessionStore;
        _options = options;
        _logger = logger;
    }

    public static string BuildPassthroughKey(string backendId, string seeingSessionId) =>
        $"passthrough:{backendId}:{seeingSessionId}";

    public static string BuildToolKey(string backendId, string taskId) =>
        $"tool:{backendId}:{taskId}";

    public async Task<AcpConnectionLease> LeaseAsync(
        string leaseKey,
        string backendId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        while (true)
        {
            if (_leases.TryGetValue(leaseKey, out var existing))
            {
                // 检查是否有宽限期可以取消
                if (TryCancelGracefulRelease(leaseKey, existing))
                {
                    existing.LastUsedUtc = DateTime.Now;
                    Interlocked.Increment(ref existing.ActiveUsers);
                    _logger.LogDebug("Reusing ACP lease {LeaseKey} from grace period", leaseKey);
                    return existing.Lease;
                }

                // 正常复用检查
                if (existing.Lease.Client.State == SubprocessClientState.Running)
                {
                    existing.LastUsedUtc = DateTime.Now;
                    Interlocked.Increment(ref existing.ActiveUsers);
                    _logger.LogDebug("Reusing ACP lease {LeaseKey}", leaseKey);
                    return existing.Lease;
                }

                _logger.LogWarning(
                    "ACP lease {LeaseKey} process is not running (state={State}), recreating",
                    leaseKey,
                    existing.Lease.Client.State);
                await ReleaseByKeyAsync(leaseKey).ConfigureAwait(false);
            }

            var gate = _promptGates.GetOrAdd(leaseKey, _ => new SemaphoreSlim(1, 1));
            var client = _clientFactory.Create(backendId);

            try
            {
                await client.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start ACP client for lease {LeaseKey}", leaseKey);
                throw;
            }

            var lease = new AcpConnectionLease(this, leaseKey, client, gate);
            var entry = new LeaseEntry
            {
                Lease = lease,
                LastUsedUtc = DateTime.Now,
                ActiveUsers = 1
            };

            if (_leases.TryAdd(leaseKey, entry))
            {
                _logger.LogInformation("Created ACP lease {LeaseKey}", leaseKey);
                return lease;
            }

            await StopClientAsync(client).ConfigureAwait(false);
        }
    }

    internal void ReturnLease(string leaseKey, AcpConnectionLease lease)
    {
        if (!_leases.TryGetValue(leaseKey, out var entry) || !ReferenceEquals(entry.Lease, lease))
            return;

        entry.LastUsedUtc = DateTime.Now;
        var remaining = Interlocked.Decrement(ref entry.ActiveUsers);
        if (remaining < 0)
            Interlocked.Exchange(ref entry.ActiveUsers, 0);

        lease.Client.ConfigureForRequest(updateSink: null, permissionContext: null);
        _logger.LogDebug("Returned ACP lease {LeaseKey} to pool (active={ActiveUsers})", leaseKey, entry.ActiveUsers);
    }

    /// <summary>
    /// 调度宽限期释放。如果在宽限期内同一 lease 再次请求，可复用进程和 ACP Session。
    /// </summary>
    /// <param name="leaseKey">租约 Key</param>
    /// <param name="seeingSessionId">Seeing Session ID</param>
    /// <param name="mapping">ACP Session 映射</param>
    public void ScheduleGracefulRelease(string leaseKey, string seeingSessionId, AcpSessionMapping mapping)
    {
        var gracePeriod = _options.Value.Acp.SessionGracePeriod;
        if (gracePeriod <= TimeSpan.Zero)
        {
            // 宽限期禁用，立即释放
            _sessionStore.ClearCachedMapping(seeingSessionId);
            _ = ReleaseByKeyAsync(leaseKey);
            return;
        }

        if (!_leases.TryGetValue(leaseKey, out var entry))
        {
            _sessionStore.ClearCachedMapping(seeingSessionId);
            return;
        }

        // 已有宽限期计时器，不重复调度
        if (entry.GracePeriodCts != null)
        {
            _logger.LogDebug("ACP lease {LeaseKey} already has grace period timer", leaseKey);
            return;
        }

        // 缓存 mapping 以便宽限期内恢复 ACP Session
        _sessionStore.CacheMapping(seeingSessionId, mapping);
        entry.SeeingSessionId = seeingSessionId;

        entry.GracePeriodCts = new CancellationTokenSource();
        var cts = entry.GracePeriodCts.Token;
        var sessionId = seeingSessionId;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(gracePeriod, cts).ConfigureAwait(false);

                _logger.LogInformation(
                    "ACP lease {LeaseKey} grace period expired after {GraceMinutes:F0} minutes, releasing",
                    leaseKey,
                    gracePeriod.TotalMinutes);

                // 清除缓存的 mapping
                _sessionStore.ClearCachedMapping(sessionId);

                await ReleaseByKeyAsync(leaseKey).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug(
                    "ACP lease {LeaseKey} grace period cancelled, lease will be reused",
                    leaseKey);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in grace period timer for lease {LeaseKey}", leaseKey);
                _sessionStore.ClearCachedMapping(sessionId);
            }
        }, CancellationToken.None);

        _logger.LogInformation(
            "Scheduled graceful release for ACP lease {LeaseKey} after {GraceMinutes:F0} minutes",
            leaseKey,
            gracePeriod.TotalMinutes);
    }

    /// <summary>
    /// 尝试取消宽限期释放。返回 true 表示成功取消（可以复用 lease）。
    /// </summary>
    /// <param name="leaseKey">租约 Key</param>
    /// <param name="entry">租约条目</param>
    /// <returns>是否成功取消宽限期</returns>
    private bool TryCancelGracefulRelease(string leaseKey, LeaseEntry entry)
    {
        var cts = entry.GracePeriodCts;
        if (cts == null)
            return false;

        try
        {
            cts.Cancel();
            cts.Dispose();
            entry.GracePeriodCts = null;
            _logger.LogDebug("Cancelled grace period for ACP lease {LeaseKey}", leaseKey);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error cancelling grace period for lease {LeaseKey}", leaseKey);
            return false;
        }
    }

    public async Task EvictIdleLeasesAsync(CancellationToken cancellationToken = default)
    {
        var idleTimeout = _options.Value.Acp.IdleTimeout;
        if (idleTimeout <= TimeSpan.Zero)
            return;

        var cutoff = DateTime.Now - idleTimeout;

        foreach (var leaseKey in _leases.Keys.ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_leases.TryGetValue(leaseKey, out var entry))
                continue;

            // 跳过正在使用中的 lease
            if (entry.ActiveUsers > 0)
                continue;

            // 跳过处于宽限期的 lease（宽限期计时器会处理）
            if (entry.GracePeriodCts != null)
                continue;

            // 跳过最近使用过的 lease
            if (entry.LastUsedUtc > cutoff)
                continue;

            _logger.LogInformation(
                "Evicting idle ACP lease {LeaseKey} after {IdleMinutes:F0} minutes of inactivity",
                leaseKey,
                idleTimeout.TotalMinutes);

            await ReleaseByKeyAsync(leaseKey).ConfigureAwait(false);
        }
    }

    public async Task ReleaseByKeyAsync(string leaseKey)
    {
        if (!_leases.TryRemove(leaseKey, out var entry))
            return;

        // 取消宽限期计时器（如果存在）
        if (entry.GracePeriodCts != null)
        {
            try
            {
                entry.GracePeriodCts.Cancel();
                entry.GracePeriodCts.Dispose();
            }
            catch { }
        }

        // 清除缓存的 mapping
        if (entry.SeeingSessionId != null)
            _sessionStore.ClearCachedMapping(entry.SeeingSessionId);

        try
        {
            await StopClientAsync(entry.Lease.Client).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error stopping ACP client for lease {LeaseKey}", leaseKey);
        }

        if (_promptGates.TryRemove(leaseKey, out var gate))
            gate.Dispose();
    }

    public async Task StopAllAsync(CancellationToken cancellationToken = default)
    {
        foreach (var key in _leases.Keys.ToList())
        {
            if (_leases.TryRemove(key, out var entry))
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 取消宽限期计时器
                if (entry.GracePeriodCts != null)
                {
                    try
                    {
                        entry.GracePeriodCts.Cancel();
                        entry.GracePeriodCts.Dispose();
                    }
                    catch { }
                }

                // 清除缓存的 mapping
                if (entry.SeeingSessionId != null)
                    _sessionStore.ClearCachedMapping(entry.SeeingSessionId);

                try
                {
                    await StopClientAsync(entry.Lease.Client, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error stopping lease {LeaseKey}", key);
                }
            }
        }

        foreach (var gate in _promptGates.Values)
            gate.Dispose();

        _promptGates.Clear();
    }

    public async ValueTask DisposeAsync() => await StopAllAsync().ConfigureAwait(false);

    private static async Task StopClientAsync(SeeingAcpClient client, CancellationToken cancellationToken = default)
    {
        await client.StopAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await client.DisposeAsync().ConfigureAwait(false);
    }
}
