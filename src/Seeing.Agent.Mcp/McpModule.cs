using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol.Client;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Abstractions.Mcp;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Mcp.Configuration;
using Seeing.Agent.Mcp.Factory;
using Seeing.Agent.Mcp.Management;
using Seeing.Agent.Mcp.Policy;

namespace Seeing.Agent.Mcp;

/// <summary>
/// MCP 模块 — 提供 McpClientManager、传输工厂与配置持久化。
/// </summary>
public sealed class McpModule : ISeeingModule
{
    private static readonly IReadOnlyList<string> s_providedSeams = ["mcp"];

    /// <inheritdoc />
    public string Id => "mcp";

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedTools { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedSeams => s_providedSeams;

    /// <inheritdoc />
    public IReadOnlyList<string> DependsOn { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddOptions<McpOptions>();

        // Interim: register MCP stack so existing discovery still works
        // without Activate until Host Shape lands.
        services.AddSingleton<McpGlobalPolicy>(sp =>
        {
            var options = sp.GetService<Microsoft.Extensions.Options.IOptions<McpOptions>>()?.Value
                ?? new McpOptions();
            var config = sp.GetService<IConfiguration>()?.GetSection(McpOptions.ConfigurationSection);
            if (config is not null)
                config.Bind(options);

            return new McpGlobalPolicy
            {
                ConnectionTimeout = TimeSpan.FromSeconds(options.ConnectionTimeoutSeconds),
                OperationTimeout = TimeSpan.FromSeconds(options.OperationTimeoutSeconds),
                BackgroundCheckInterval = TimeSpan.FromSeconds(options.BackgroundCheckIntervalSeconds),
                MaxConcurrentConnections = options.MaxConcurrentConnections,
                AutoStartOnAdd = options.AutoStartOnAdd
            };
        });

        services.AddSingleton<McpWrapperFactoryRegistry>(sp =>
        {
            var registry = new McpWrapperFactoryRegistry();
            registry.Register(new StdioWrapperFactory());
            registry.Register(new HttpWrapperFactory(HttpTransportMode.StreamableHttp));
            registry.Register(new HttpWrapperFactory(HttpTransportMode.Sse));
            return registry;
        });

        services.AddSingleton<McpToolRegistry>(sp =>
        {
            var toolInvoker = sp.GetRequiredService<IToolManager>();
            var hookManager = sp.GetRequiredService<IHookManager>();
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<McpToolRegistry>();
            return new McpToolRegistry(toolInvoker, hookManager, logger);
        });

        services.AddSingleton<McpProcessMonitor>(sp =>
        {
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<McpProcessMonitor>();
            return new McpProcessMonitor(logger);
        });

        services.AddSingleton<IMcpConfigPersistence, McpConfigPersistence>();
        services.AddSingleton<IMcpManager, McpClientManager>();
        services.AddSingleton<McpClientManager>(sp => (McpClientManager)sp.GetRequiredService<IMcpManager>());
    }

    /// <inheritdoc />
    public Task ActivateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task DeactivateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
