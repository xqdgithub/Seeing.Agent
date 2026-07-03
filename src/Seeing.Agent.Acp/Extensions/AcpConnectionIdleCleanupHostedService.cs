using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Acp.Transport;
using Seeing.Agent.Configuration;

namespace Seeing.Agent.Acp.Extensions;

/// <summary>
/// 定期回收超过 <see cref="SeeingAgentOptions.Acp.IdleTimeout"/> 未使用的 ACP 子进程租约。
/// </summary>
internal sealed class AcpConnectionIdleCleanupHostedService : BackgroundService
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(1);

    private readonly AcpConnectionManager _connectionManager;
    private readonly IOptions<SeeingAgentOptions> _options;
    private readonly ILogger<AcpConnectionIdleCleanupHostedService> _logger;

    public AcpConnectionIdleCleanupHostedService(
        AcpConnectionManager connectionManager,
        IOptions<SeeingAgentOptions> options,
        ILogger<AcpConnectionIdleCleanupHostedService> logger)
    {
        _connectionManager = connectionManager;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Value.Acp.Enabled)
            return;

        _logger.LogDebug(
            "ACP idle lease cleanup started (interval={Interval}, idleTimeout={IdleTimeout})",
            CleanupInterval,
            _options.Value.Acp.IdleTimeout);

        using var timer = new PeriodicTimer(CleanupInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                await _connectionManager.EvictIdleLeasesAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
