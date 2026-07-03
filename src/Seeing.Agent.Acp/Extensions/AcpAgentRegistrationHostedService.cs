using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Acp.Backends;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Interfaces;

namespace Seeing.Agent.Acp.Extensions;

/// <summary>
/// 宿主启动时根据 ACP 后端配置注册 Passthrough Agent。
/// </summary>
internal sealed class AcpAgentRegistrationHostedService : IHostedService
{
    private readonly IAgentRegistry _agentRegistry;
    private readonly IAcpBackendRegistry _backendRegistry;
    private readonly IOptions<SeeingAgentOptions> _options;
    private readonly ILogger<AcpAgentRegistrationHostedService> _logger;

    public AcpAgentRegistrationHostedService(
        IAgentRegistry agentRegistry,
        IAcpBackendRegistry backendRegistry,
        IOptions<SeeingAgentOptions> options,
        ILogger<AcpAgentRegistrationHostedService> logger)
    {
        _agentRegistry = agentRegistry;
        _backendRegistry = backendRegistry;
        _options = options;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken) =>
        Hosting.AcpDynamicAgentRegistrar.RegisterAsync(
            _agentRegistry,
            _backendRegistry,
            _options,
            _logger,
            cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
