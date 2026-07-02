using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Configuration;

namespace Seeing.Agent.Gateway.Hosting;

/// <summary>
/// Gateway 服务实现，封装 <see cref="GatewayHost"/> 生命周期。
/// </summary>
public sealed class GatewayServer : IGatewayServer, IAsyncDisposable
{
    private readonly IServiceProvider _services;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<GatewayServer> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private GatewayHost? _host;

    public GatewayServer(
        IServiceProvider services,
        IOptions<GatewayOptions> options,
        ILoggerFactory loggerFactory,
        ILogger<GatewayServer> logger)
    {
        _services = services;
        _options = options;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsRunning => _host != null;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var gatewayOptions = _options.Value;
        if (!gatewayOptions.Enabled)
        {
            _logger.LogDebug("Gateway 未启用（SeeingAgent:Gateway:Enabled=false），跳过启动");
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_host != null)
                return;

            _host = new GatewayHost(
                _services,
                gatewayOptions,
                _loggerFactory.CreateLogger<GatewayHost>());

            await _host.StartAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Gateway 已启动: http://{BindAddress}:{Port}",
                gatewayOptions.BindAddress,
                gatewayOptions.Port);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_host == null)
                return;

            await _host.StopAsync().ConfigureAwait(false);
            await _host.DisposeAsync().ConfigureAwait(false);
            _host = null;

            _logger.LogInformation("Gateway 已停止");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
