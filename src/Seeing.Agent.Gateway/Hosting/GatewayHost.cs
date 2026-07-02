using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Configuration;
using Seeing.Agent.Gateway.Core;
using Seeing.Agent.Gateway.Endpoints;
using Seeing.Agent.Gateway.Permission;
using Seeing.Session.Management;

namespace Seeing.Agent.Gateway.Hosting;

/// <summary>
/// 独立 Kestrel 宿主，在 Extension InitializeAsync 内启动。
/// </summary>
public sealed class GatewayHost : IAsyncDisposable
{
    private readonly IServiceProvider _rootServices;
    private readonly GatewayOptions _options;
    private readonly ILogger<GatewayHost> _logger;

    private WebApplication? _app;
    private Task? _runTask;
    private CancellationTokenSource? _hostCts;

    public GatewayHost(
        IServiceProvider rootServices,
        GatewayOptions options,
        ILogger<GatewayHost> logger)
    {
        _rootServices = rootServices;
        _options = options;
        _logger = logger;
    }

    /// <summary>启动 Gateway HTTP 服务</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(GatewayHost).Assembly.FullName
        });

        builder.WebHost.UseUrls($"http://{_options.BindAddress}:{_options.Port}");

        builder.Services.Configure<KestrelServerOptions>(options =>
        {
            options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(30);
            options.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(30);
        });

        builder.Services.Configure<HostOptions>(options =>
        {
            options.ShutdownTimeout = TimeSpan.FromSeconds(30);
        });

        var permissionChannel = new GatewayPermissionChannel(_options);
        var runTracker = new GatewayRunTracker();
        var executionQueue = new SessionExecutionQueue();
        var connectionManager = new GatewayConnectionManager();
        var sessionManager = _rootServices.GetRequiredService<SessionManager>();
        var sessionResolver = new GatewaySessionResolver(sessionManager, _options);
        var loggerFactory = _rootServices.GetRequiredService<ILoggerFactory>();
        var orchestratorLogger = loggerFactory.CreateLogger<GatewayOrchestrator>();

        var orchestrator = new GatewayOrchestrator(
            _rootServices,
            _options,
            permissionChannel,
            runTracker,
            executionQueue,
            sessionResolver,
            orchestratorLogger,
            connectionManager);

        builder.Services.AddSingleton(_options);
        builder.Services.AddSingleton(permissionChannel);
        builder.Services.AddSingleton(runTracker);
        builder.Services.AddSingleton(executionQueue);
        builder.Services.AddSingleton(connectionManager);
        builder.Services.AddSingleton(orchestrator);
        builder.Services.AddSingleton(sp => new GatewayWebSocketHandler(
            orchestrator,
            permissionChannel,
            connectionManager,
            _options,
            loggerFactory.CreateLogger<GatewayWebSocketHandler>()));

        var app = builder.Build();
        app.MapGatewayEndpoints();

        if (_options.EnableWebSocket)
        {
            app.UseWebSockets(new WebSocketOptions
            {
                KeepAliveInterval = TimeSpan.FromSeconds(Math.Max(5, _options.WebSocketKeepAliveSeconds))
            });

            app.Map(_options.WebSocketPath, async (HttpContext context, GatewayWebSocketHandler handler) =>
            {
                await handler.HandleAsync(context);
            });
        }

        _hostCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _app = app;
        _runTask = app.RunAsync();

        _logger.LogInformation(
            "GatewayHost listening on http://{BindAddress}:{Port}{WebSocketPath}",
            _options.BindAddress,
            _options.Port,
            _options.EnableWebSocket ? $" (WS: {_options.WebSocketPath})" : string.Empty);

        return Task.CompletedTask;
    }

    /// <summary>停止 Gateway HTTP 服务</summary>
    public async Task StopAsync()
    {
        if (_app == null)
            return;

        try
        {
            _hostCts?.Cancel();
            await _app.StopAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // expected during shutdown
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GatewayHost 停止时出现异常");
        }

        if (_runTask != null)
        {
            try
            {
                await _runTask;
            }
            catch (OperationCanceledException)
            {
                // expected
            }
        }

        await _app.DisposeAsync();
        _hostCts?.Dispose();
        _app = null;
        _runTask = null;
        _hostCts = null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await StopAsync();
}
