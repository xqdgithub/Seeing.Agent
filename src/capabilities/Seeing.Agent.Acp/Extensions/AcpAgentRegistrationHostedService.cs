using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Acp.Backends;
using Seeing.Agent.Acp.Configuration;
using Seeing.Agent.Acp.Hosting;

namespace Seeing.Agent.Acp.Extensions;

/// <summary>
/// 宿主启动时根据 ACP 后端配置注册 Passthrough Agent。
/// </summary>
internal sealed class AcpAgentRegistrationHostedService : IHostedService
{
    public const string ModuleId = "acp";

    private readonly IAgentRegistry _agentRegistry;
    private readonly IAcpBackendRegistry _backendRegistry;
    private readonly IOptionsMonitor<AcpOptions> _options;
    private readonly AcpModuleActivity _activity;
    private readonly IModuleCatalog? _catalog;
    private readonly ILogger<AcpAgentRegistrationHostedService> _logger;

    public AcpAgentRegistrationHostedService(
        IAgentRegistry agentRegistry,
        IAcpBackendRegistry backendRegistry,
        IOptionsMonitor<AcpOptions> options,
        AcpModuleActivity activity,
        ILogger<AcpAgentRegistrationHostedService> logger,
        IModuleCatalog? catalog = null)
    {
        _agentRegistry = agentRegistry;
        _backendRegistry = backendRegistry;
        _options = options;
        _activity = activity;
        _logger = logger;
        _catalog = catalog;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_catalog is not null && !_catalog.IsEnabled(ModuleId))
            return Task.CompletedTask;

        if (!_activity.IsActive && _catalog is not null)
            return Task.CompletedTask;

        return Hosting.AcpDynamicAgentRegistrar.RegisterAsync(
            _agentRegistry,
            _backendRegistry,
            _options,
            _logger,
            cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
