using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Gateway.Configuration;
using Seeing.Agent.Gateway.Core;
using Seeing.Agent.Gateway.Endpoints;
using Seeing.Agent.Gateway.Permission;
using Seeing.Agent.Abstractions.Scheduling;
using Seeing.Agent.Llm;
using Seeing.Session.Core;

namespace Seeing.Agent.Gateway.Hosting;

/// <summary>
/// 独立 Kestrel 宿主，在 Extension InitializeAsync 内启动。
/// </summary>
public sealed class GatewayHost : IAsyncDisposable
{
    private readonly IServiceProvider _rootServices;
    private readonly IOptions<GatewayOptions> _optionsMonitor;
    private readonly ILogger<GatewayHost> _logger;

    private WebApplication? _app;
    private Task? _runTask;
    private CancellationTokenSource? _hostCts;

    /// <summary>实时读取配置（GatewayOptionsMonitor），避免启动时固化快照。</summary>
    private GatewayOptions Options => _optionsMonitor.Value;

    public GatewayHost(
        IServiceProvider rootServices,
        IOptions<GatewayOptions> options,
        ILogger<GatewayHost> logger)
    {
        _rootServices = rootServices;
        _optionsMonitor = options;
        _logger = logger;
    }

    /// <summary>启动 Gateway HTTP 服务</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(GatewayHost).Assembly.FullName
        });

        builder.WebHost.UseUrls($"http://{Options.BindAddress}:{Options.Port}");

        builder.Services.Configure<KestrelServerOptions>(options =>
        {
            options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(30);
            options.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(30);
        });

        builder.Services.Configure<HostOptions>(options =>
        {
            options.ShutdownTimeout = TimeSpan.FromSeconds(30);
        });

        // 根容器解析同一 GatewayPermissionChannel 实例（具体类型 + 接口映射均在 AddSeeingGatewayServer 完成）
        var permissionChannel = _rootServices.GetRequiredService<GatewayPermissionChannel>();
        var permissionManager = _rootServices.GetRequiredService<IPermissionRequestManager>();
        var runTracker = new GatewayRunTracker();
        var executionQueue = new SessionExecutionQueue();
        var connectionManager = _rootServices.GetRequiredService<GatewayConnectionManager>();
        var sessionManager = _rootServices.GetRequiredService<ISessionManager>();
        var selectionResolver = _rootServices.GetRequiredService<IAgentSelectionResolver>();
        var agentRegistry = _rootServices.GetRequiredService<IAgentRegistry>();
        var runtimeManager = _rootServices.GetRequiredService<IAgentRuntimeManager>();
        var modelManager = _rootServices.GetRequiredService<IModelManager>();
        var defaultWorkMode = _rootServices.GetService<IDefaultWorkModeProvider>();
        var sessionResolver = new GatewaySessionResolver(
            sessionManager, selectionResolver, modelManager, defaultWorkMode);
        var sessionService = new GatewaySessionService(sessionManager, agentRegistry, runtimeManager, modelManager);
        var loggerFactory = _rootServices.GetRequiredService<ILoggerFactory>();
        var orchestratorLogger = loggerFactory.CreateLogger<GatewayOrchestratorV2>();

        var orchestrator = new GatewayOrchestratorV2(
            _rootServices,
            Options,
            runTracker,
            executionQueue,
            orchestratorLogger);

        builder.Services.AddSingleton(Options);
        builder.Services.AddSingleton(permissionChannel);
        builder.Services.AddSingleton(permissionManager);
        builder.Services.AddSingleton(runTracker);
        builder.Services.AddSingleton(executionQueue);
        builder.Services.AddSingleton(connectionManager);
        builder.Services.AddSingleton(orchestrator);
        builder.Services.AddSingleton(sessionService);
        builder.Services.AddSingleton(sessionResolver);
        // Register root-level services for admin endpoints
        builder.Services.AddSingleton(_rootServices.GetRequiredService<ISessionManager>());
        var scheduleStatus = _rootServices.GetService<IScheduleStatusQuery>();
        if (scheduleStatus is not null)
            builder.Services.AddSingleton(scheduleStatus);
        builder.Services.AddSingleton(sp => new GatewayWebSocketHandler(
            orchestrator,
            permissionManager,
            connectionManager,
            Options,
            loggerFactory.CreateLogger<GatewayWebSocketHandler>()));

        var app = builder.Build();
        app.MapGatewayEndpoints();
        app.MapAdminEndpoints();

        if (Options.EnableWebSocket)
        {
            app.UseWebSockets(new WebSocketOptions
            {
                KeepAliveInterval = TimeSpan.FromSeconds(Math.Max(5, Options.WebSocketKeepAliveSeconds))
            });

            app.Map(Options.WebSocketPath, async (HttpContext context, GatewayWebSocketHandler handler) =>
            {
                await handler.HandleAsync(context);
            });
        }

        _hostCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _app = app;
        await app.StartAsync(_hostCts.Token).ConfigureAwait(false);
        _runTask = app.WaitForShutdownAsync(CancellationToken.None);

        _logger.LogInformation(
            "GatewayHost listening on http://{BindAddress}:{Port}{WebSocketPath}",
            Options.BindAddress,
            Options.Port,
            Options.EnableWebSocket ? $" (WS: {Options.WebSocketPath})" : string.Empty);
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
