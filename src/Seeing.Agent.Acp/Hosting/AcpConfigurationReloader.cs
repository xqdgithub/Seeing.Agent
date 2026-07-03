using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Acp.Backends;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Interfaces;

namespace Seeing.Agent.Acp.Hosting;

/// <summary>
/// 在 ACP 配置变更后重新注册 Passthrough Agent。
/// </summary>
public interface IAcpConfigurationReloader
{
    Task ReloadAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class AcpConfigurationReloader : IAcpConfigurationReloader
{
    private readonly IAgentRegistry _agentRegistry;
    private readonly IAcpBackendRegistry _backendRegistry;
    private readonly IOptions<SeeingAgentOptions> _options;
    private readonly ILogger<AcpConfigurationReloader> _logger;

    public AcpConfigurationReloader(
        IAgentRegistry agentRegistry,
        IAcpBackendRegistry backendRegistry,
        IOptions<SeeingAgentOptions> options,
        ILogger<AcpConfigurationReloader> logger)
    {
        _agentRegistry = agentRegistry;
        _backendRegistry = backendRegistry;
        _options = options;
        _logger = logger;
    }

    public Task ReloadAsync(CancellationToken cancellationToken = default) =>
        AcpDynamicAgentRegistrar.RegisterAsync(
            _agentRegistry,
            _backendRegistry,
            _options,
            _logger,
            cancellationToken);
}
