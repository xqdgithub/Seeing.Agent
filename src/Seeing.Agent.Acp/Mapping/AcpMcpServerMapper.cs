using Acp.Types;
using AcpMcpServerConfig = Acp.Types.McpServerConfig;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Configuration;
using Seeing.Agent.MCP;
using SeeingMcpConfig = Seeing.Agent.MCP.McpServerConfig;

namespace Seeing.Agent.Acp.Mapping;

/// <summary>
/// 将 Seeing MCP 配置映射为 ACP McpServerConfig。
/// </summary>
public sealed class AcpMcpServerMapper
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly IOptions<SeeingAgentOptions> _options;
    private readonly ILogger<AcpMcpServerMapper> _logger;

    public AcpMcpServerMapper(
        IServiceProvider serviceProvider,
        IWorkspaceProvider workspaceProvider,
        IOptions<SeeingAgentOptions> options,
        ILogger<AcpMcpServerMapper> logger)
    {
        _serviceProvider = serviceProvider;
        _workspaceProvider = workspaceProvider;
        _options = options;
        _logger = logger;
    }

    public Task<List<AcpMcpServerConfig>> MapAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_options.Value.Acp.Enabled)
            return Task.FromResult(new List<AcpMcpServerConfig>());

        var result = new List<AcpMcpServerConfig>();

        // 延迟解析 McpClientManager，避免 McpClientManager → ToolManager → AcpTool → … → 本类 的构造期死锁
        var manager = _serviceProvider.GetService<McpClientManager>();
        if (manager != null)
        {
            foreach (var (name, config) in manager.GetAllConfigs())
            {
                var mapped = AcpMcpServerMapping.TryMap(name, config, _logger);
                if (mapped != null)
                    result.Add(mapped);
            }
        }
        else
        {
            foreach (var config in McpConfigLoader.LoadDefault(_workspaceProvider, _logger))
            {
                if (string.IsNullOrWhiteSpace(config.Name))
                    continue;

                var mapped = AcpMcpServerMapping.TryMap(config.Name, config, _logger);
                if (mapped != null)
                    result.Add(mapped);
            }
        }

        _logger.LogDebug("Mapped {Count} MCP servers for ACP session", result.Count);
        return Task.FromResult(result);
    }
}
