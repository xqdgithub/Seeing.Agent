using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Acp.Backends;
using Seeing.Agent.Acp.Extensions;
using Seeing.Agent.Acp.Hosting;
using Seeing.Agent.Acp.Tools;
using Seeing.Agent.Acp.Transport;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Interfaces;

namespace Seeing.Agent.Acp;

/// <summary>
/// ACP 插件入口。
/// </summary>
public sealed class AcpExtension : IExtension
{
    /// <inheritdoc />
    public string? Id => "seeing.agent.acp";

    /// <inheritdoc />
    public string Version => "1.0.0";

    /// <inheritdoc />
    public string Name => "Seeing.Agent ACP";

    /// <inheritdoc />
    public string Description => "ACP 双模式集成（Passthrough 透传 + acp 工具委派）";

    /// <inheritdoc />
    public string Target => "server";

    private AcpConnectionManager? _connectionManager;
    private AcpTool? _acpTool;
    private AcpStatusTool? _acpStatusTool;
    private ILogger? _logger;

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services) => services.AddSeeingAcp();

    /// <inheritdoc />
    public async Task InitializeAsync(ExtensionContext context, ExtensionMeta meta)
    {
        var loggerFactory = context.Services.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger<AcpExtension>();

        var options = context.Services.GetRequiredService<IOptions<SeeingAgentOptions>>().Value;
        if (!options.Acp.Enabled)
        {
            _logger.LogInformation("{Name} loaded but ACP is disabled in configuration", Name);
        }
        else if (options.Acp.Backends.Count == 0)
        {
            _logger.LogWarning("{Name} enabled but no ACP backends configured", Name);
        }
        else
        {
            _logger.LogInformation(
                "{Name} initialized with {Count} backend(s), default={Default}",
                Name,
                options.Acp.Backends.Count,
                options.Acp.DefaultBackend ?? "(first enabled)");

            await AcpDynamicAgentRegistrar.RegisterAsync(
                context.AgentRegistry,
                context.Services.GetRequiredService<IAcpBackendRegistry>(),
                context.Services.GetRequiredService<IOptions<SeeingAgentOptions>>(),
                _logger);
        }

        _connectionManager = context.Services.GetService<AcpConnectionManager>();
        _acpTool = context.Services.GetService<AcpTool>();
        _acpStatusTool = context.Services.GetService<AcpStatusTool>();

        if (_connectionManager == null || _acpTool == null)
        {
            _logger.LogWarning(
                "ACP services not registered in DI. Call services.AddSeeingAcp() before building the host.");
        }
        else
        {
            var lifecycleHook = context.Services.GetService<AcpSessionLifecycleHook>();
            if (lifecycleHook != null)
                context.HookManager.RegisterMulti(lifecycleHook);
        }
    }

    /// <inheritdoc />
    public IEnumerable<ITool> GetTools()
    {
        if (_acpTool != null)
            yield return _acpTool;
        if (_acpStatusTool != null)
            yield return _acpStatusTool;
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        _logger?.LogInformation("Stopping {Name}", Name);

        if (_connectionManager != null)
            await _connectionManager.StopAllAsync().ConfigureAwait(false);
    }
}
