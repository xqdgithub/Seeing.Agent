using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol.Client;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Abstractions.Components;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Abstractions.Mcp;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Abstractions.Ui;
using Seeing.Agent.Configuration;
using Seeing.Agent.Mcp.Commands;
using Seeing.Agent.Mcp.Configuration;
using Seeing.Agent.Mcp.Factory;
using Seeing.Agent.Mcp.Management;
using Seeing.Agent.Mcp.OAuth;
using Seeing.Agent.Mcp.Policy;

namespace Seeing.Agent.Mcp;

/// <summary>
/// MCP 模块 — 提供 McpClientManager、传输工厂与配置持久化。
/// </summary>
public sealed class McpModule : ISeeingModule, IUiContribution
{
    private static readonly IReadOnlyList<string> s_providedSeams = ["mcp"];

    private readonly IUiContributionRegistry? _ui;
    private readonly List<string> _registeredCommandNames = new();

    /// <summary>无依赖实例仅用于 <see cref="ConfigureServices"/>。</summary>
    public McpModule()
    {
    }

    /// <summary>DI 解析用。</summary>
    public McpModule(IUiContributionRegistry? uiRegistry)
    {
        _ui = uiRegistry;
    }

    /// <inheritdoc />
    public string Id => "mcp";

    /// <inheritdoc />
    public string ModuleId => Id;

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedTools { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedSeams => s_providedSeams;

    /// <inheritdoc />
    public IReadOnlyList<string> DependsOn { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        TryRegisterConfigSection(services);
        services.AddOptions<McpOptions>();

        // OAuth 宿主接线：注册 Provider/存储/令牌客户端/授权器/浏览器打开器
        services.AddMcpOAuth();

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
            // OAuth 服务存在时复用于 HTTP 工厂，避免重复创建存储/令牌客户端
            var oauthStorage = sp.GetService<McpOAuthStorage>();
            var oauthTokenClient = sp.GetService<McpOAuthTokenClient>();
            registry.Register(new StdioWrapperFactory());
            registry.Register(new HttpWrapperFactory(HttpTransportMode.StreamableHttp, oauthStorage, oauthTokenClient));
            registry.Register(new HttpWrapperFactory(HttpTransportMode.Sse, oauthStorage, oauthTokenClient));
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
        services.AddSingleton<IComponentLoader, McpLoader>();
    }

    /// <inheritdoc />
    public IReadOnlyList<object> Contribute() =>
    [
        new NavContribution("/mcp", "MCP", "api", ["mcp"],
            Group: NavGroups.Workspace, GroupIcon: NavGroups.WorkspaceIcon, Order: 30),
    ];

    /// <inheritdoc />
    public async Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        _ui?.Register(this);

        // 初始化连接（配置加载复用 McpLoader 的静态入口）
        var manager = services.GetService<IMcpManager>();
        if (manager is not null)
        {
            var configs = await LoadConfigsAsync(services, cancellationToken).ConfigureAwait(false);
            await manager.InitializeAsync(configs, cancellationToken).ConfigureAwait(false);
        }

        // 登记 OAuth 授权命令（配置在 InitializeAsync 后才可用）
        RegisterCommands(services);

        // 登记动态工具贡献（工具集运行时可变，结算时并入 settledToolIds）
        services.GetService<IDynamicToolContributorRegistry>()
            ?.Register(new McpDynamicToolContributor(manager));
    }

    /// <inheritdoc />
    public async Task DeactivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        UnregisterCommands(services);

        _ui?.Unregister(Id);

        services.GetService<IDynamicToolContributorRegistry>()?.Unregister(Id);

        var manager = services.GetService<IMcpManager>();
        if (manager is not null)
        {
            await manager.ShutdownAsync(cancellationToken).ConfigureAwait(false);
            await manager.UnregisterAllToolsAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>登记 <c>/mcp-auth</c> OAuth 授权命令（缺少授权器或命令注册表时静默跳过）。</summary>
    private void RegisterCommands(IServiceProvider services)
    {
        if (services.GetService<ICommandRegistry>() is not { } registry
            || services.GetService<IMcpOAuthAuthorizer>() is not { } authorizer)
        {
            return;
        }

        var command = new McpAuthCommand(authorizer);
        registry.Register(command);

        _registeredCommandNames.Clear();
        _registeredCommandNames.Add(command.Metadata.Name);
    }

    /// <summary>注销本模块登记的命令。</summary>
    private void UnregisterCommands(IServiceProvider services)
    {
        if (_registeredCommandNames.Count == 0)
            return;

        if (services.GetService<ICommandRegistry>() is { } registry)
        {
            foreach (var name in _registeredCommandNames)
                registry.Unregister(name);
        }

        _registeredCommandNames.Clear();
    }

    /// <summary>加载 MCP 配置（复用 <see cref="McpConfigLoader"/>）；无目录信息时返回空集。</summary>
    private static async Task<IReadOnlyDictionary<string, McpServerConfig>> LoadConfigsAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        ISeeingDirectories? directories = services.GetService<ISeeingDirectories>();
        if (directories is null)
        {
            var workspaceRoot = services.GetService<IWorkspaceProvider>()?.GetProjectRoot();
            if (string.IsNullOrWhiteSpace(workspaceRoot))
                return new Dictionary<string, McpServerConfig>();

            directories = new WorkspaceSeeingDirectories(workspaceRoot);
        }

        var logger = services.GetService<ILoggerFactory>()?.CreateLogger<McpLoader>();
        var configs = await McpConfigLoader.LoadDefaultAsync(directories, logger, cancellationToken)
            .ConfigureAwait(false);

        var dict = new Dictionary<string, McpServerConfig>();
        foreach (var config in configs)
        {
            if (!string.IsNullOrEmpty(config.Name))
                dict[config.Name] = config;
        }

        return dict;
    }

    /// <summary>MCP 动态工具贡献者 — 从当前已注册 MCP 工具派生工具 id。</summary>
    private sealed class McpDynamicToolContributor : IDynamicToolContributor
    {
        private readonly IMcpManager? _manager;

        public McpDynamicToolContributor(IMcpManager? manager) => _manager = manager;

        public string ModuleId => "mcp";

        public IReadOnlyCollection<string> GetDynamicToolIds()
        {
            if (_manager is null)
                return Array.Empty<string>();

            return _manager.GetTools()
                .Where(t => !string.IsNullOrEmpty(t.ServerName) && !string.IsNullOrEmpty(t.Name))
                .Select(t => $"{t.ServerName}_{t.Name}")
                .ToArray();
        }
    }

    private static void TryRegisterConfigSection(IServiceCollection services)
    {
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == typeof(IConfigSectionRegistry) &&
                descriptor.ImplementationInstance is IConfigSectionRegistry registry)
            {
                registry.Register(new ConfigSectionMeta(
                    "Mcp",
                    "mcp.json",
                    ConfigScope.Both,
                    typeof(Dictionary<string, McpServerConfig>)));
                return;
            }
        }
    }
}
