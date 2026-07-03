using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Acp.Configuration;
using Seeing.Agent.Configuration;

namespace Seeing.Agent.Acp.Backends;

/// <summary>
/// 从 <see cref="SeeingAgentOptions.Acp"/> 解析后端配置。
/// </summary>
public sealed class AcpBackendRegistry : IAcpBackendRegistry
{
    private readonly SeeingAgentConfigurationProvider _configProvider;
    private readonly ILogger<AcpBackendRegistry> _logger;

    public AcpBackendRegistry(
        SeeingAgentConfigurationProvider configProvider,
        ILogger<AcpBackendRegistry> logger)
    {
        _configProvider = configProvider;
        _logger = logger;
    }

    private CoreAcpOptions Acp => _configProvider.Options.Acp;

    /// <inheritdoc />
    public AcpBackendDescriptor GetBackend(string backendId)
    {
        if (!TryGetBackend(backendId, out var descriptor) || descriptor == null)
        {
            throw new InvalidOperationException($"ACP backend '{backendId}' is not configured or disabled.");
        }

        return descriptor;
    }

    /// <inheritdoc />
    public IReadOnlyList<AcpBackendDescriptor> GetEnabledBackends()
    {
        if (!Acp.Enabled)
            return Array.Empty<AcpBackendDescriptor>();

        return Acp.Backends
            .Select(kvp => TryCreateDescriptor(kvp.Key, kvp.Value))
            .Where(d => d is { Enabled: true })
            .Cast<AcpBackendDescriptor>()
            .ToList();
    }

    /// <inheritdoc />
    public string ResolveDefault(string? preferredBackend = null)
    {
        if (!string.IsNullOrWhiteSpace(preferredBackend))
            return GetBackend(preferredBackend).Id;

        if (!string.IsNullOrWhiteSpace(Acp.DefaultBackend))
            return GetBackend(Acp.DefaultBackend).Id;

        var enabled = GetEnabledBackends();
        if (enabled.Count == 0)
            throw new InvalidOperationException("No enabled ACP backends configured.");

        return enabled[0].Id;
    }

    /// <inheritdoc />
    public bool TryGetBackend(string backendId, out AcpBackendDescriptor? descriptor)
    {
        descriptor = null;

        if (!Acp.Enabled)
            return false;

        if (!Acp.Backends.TryGetValue(backendId, out var config))
            return false;

        descriptor = TryCreateDescriptor(backendId, config);
        return descriptor is { Enabled: true };
    }

    private AcpBackendDescriptor? TryCreateDescriptor(string id, CoreAcpBackendConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Command))
        {
            _logger.LogWarning("ACP backend '{BackendId}' has no command configured", id);
            return null;
        }

        var resolvedCommand = AcpExecutableResolver.Resolve(config.Command);
        if (!Path.IsPathRooted(resolvedCommand))
        {
            _logger.LogWarning(
                "ACP backend '{BackendId}' command '{Command}' is not an absolute path; configure the full executable path in SeeingAgent:Acp:Backends",
                id,
                config.Command);
        }
        else if (!File.Exists(resolvedCommand))
        {
            _logger.LogWarning(
                "ACP backend '{BackendId}' command '{Command}' does not exist",
                id,
                resolvedCommand);
        }

        return new AcpBackendDescriptor
        {
            Id = id,
            Enabled = true,
            Command = resolvedCommand,
            Args = config.Args ?? new List<string>(),
            Environment = config.Environment ?? new Dictionary<string, string>()
        };
    }
}
