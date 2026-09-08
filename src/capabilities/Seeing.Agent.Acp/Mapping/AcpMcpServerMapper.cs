using Seeing.Agent.Abstractions.Mcp;
using Acp.Types;
using AcpMcpServerConfig = Acp.Types.McpServerConfig;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Acp.Configuration;

namespace Seeing.Agent.Acp.Mapping;

/// <summary>
/// 将 Seeing MCP 配置映射为 ACP McpServerConfig。
/// 仅通过 <see cref="IMcpManager"/> 读取已加载配置；未注册时跳过文件回退加载。
/// </summary>
public sealed class AcpMcpServerMapper
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IOptionsMonitor<AcpOptions> _options;
    private readonly ILogger<AcpMcpServerMapper> _logger;

    public AcpMcpServerMapper(
        IServiceProvider serviceProvider,
        IOptionsMonitor<AcpOptions> options,
        ILogger<AcpMcpServerMapper> logger)
    {
        _serviceProvider = serviceProvider;
        _options = options;
        _logger = logger;
    }

    public Task<List<AcpMcpServerConfig>> MapAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_options.CurrentValue.Enabled)
            return Task.FromResult(new List<AcpMcpServerConfig>());

        var result = new List<AcpMcpServerConfig>();

        // 延迟解析 IMcpManager，避免 McpClientManager → ToolManager → AcpTool → … → 本类 的构造期死锁
        var manager = _serviceProvider.GetService<IMcpManager>();
        if (manager is null)
        {
            // 无 IMcpManager 时不做文件回退加载；MCP 配置需由宿主模块注册并加载后才能映射到 ACP
            _logger.LogDebug("IMcpManager not registered; skipping MCP server mapping for ACP");
            return Task.FromResult(result);
        }

        foreach (var (name, config) in manager.GetAllConfigs())
        {
            var mapped = AcpMcpServerMapping.TryMap(name, config, _logger);
            if (mapped != null)
                result.Add(mapped);
        }

        _logger.LogDebug("Mapped {Count} MCP servers for ACP session", result.Count);
        return Task.FromResult(result);
    }
}
