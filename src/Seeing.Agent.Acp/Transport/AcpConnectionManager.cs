using System.Collections.Concurrent;
using Acp.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Acp.Client;
using Seeing.Agent.Configuration;

namespace Seeing.Agent.Acp.Transport;

/// <summary>
/// 管理 ACP 子进程租约（passthrough:{backend}:{sessionId} / tool:{backend}:{taskId}）。
/// 同一租约在 <see cref="SeeingAgentOptions.Acp.IdleTimeout"/> 内可复用，超时后由后台任务回收。
/// </summary>
public sealed class AcpConnectionManager : IAsyncDisposable
{
    private sealed class LeaseEntry
    {
        public required AcpConnectionLease Lease { get; init; }

        public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;

        public int ActiveUsers;
    }

    private readonly SeeingAcpClientFactory _clientFactory;
    private readonly IOptions<SeeingAgentOptions> _options;
    private readonly ILogger<AcpConnectionManager> _logger;
    private readonly ConcurrentDictionary<string, LeaseEntry> _leases = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _promptGates = new();

    public AcpConnectionManager(
        SeeingAcpClientFactory clientFactory,
        IOptions<SeeingAgentOptions> options,
        ILogger<AcpConnectionManager> logger)
    {
        _clientFactory = clientFactory;
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
                if (existing.Lease.Client.State == SubprocessClientState.Running)
                {
                    existing.LastUsedUtc = DateTime.UtcNow;
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
                LastUsedUtc = DateTime.UtcNow,
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

        entry.LastUsedUtc = DateTime.UtcNow;
        var remaining = Interlocked.Decrement(ref entry.ActiveUsers);
        if (remaining < 0)
            Interlocked.Exchange(ref entry.ActiveUsers, 0);

        lease.Client.ConfigureForRequest(updateSink: null, permissionContext: null);
        _logger.LogDebug("Returned ACP lease {LeaseKey} to pool (active={ActiveUsers})", leaseKey, entry.ActiveUsers);
    }

    public async Task EvictIdleLeasesAsync(CancellationToken cancellationToken = default)
    {
        var idleTimeout = _options.Value.Acp.IdleTimeout;
        if (idleTimeout <= TimeSpan.Zero)
            return;

        var cutoff = DateTime.UtcNow - idleTimeout;

        foreach (var leaseKey in _leases.Keys.ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_leases.TryGetValue(leaseKey, out var entry))
                continue;

            if (entry.ActiveUsers > 0 || entry.LastUsedUtc > cutoff)
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
