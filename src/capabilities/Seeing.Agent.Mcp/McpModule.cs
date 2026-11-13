using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Abstractions.Components;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Abstractions.Mcp;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Skills;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Abstractions.Ui;
using Seeing.Agent.Configuration;
using Seeing.Agent.Mcp.Commands;
using Seeing.Agent.Mcp.Configuration;
using Seeing.Agent.Mcp.Factory;
using Seeing.Agent.Mcp.Management;
using Seeing.Agent.Mcp.OAuth;
using Seeing.Agent.Mcp.Policy;
using Seeing.Agent.Mcp.Skills;
using Seeing.Agent.Mcp.Tools;
using Seeing.Agent.Mcp.Tools.Manage;
using Seeing.Agent.Mcp.Tools.Resources;
using Seeing.Agent.Mcp.Workspace;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Seeing.Agent.Mcp;

/// <summary>
/// MCP 模块 — 提供 McpClientManager、传输工厂与配置持久化。
/// </summary>
public sealed class McpModule : ISeeingModule, IUiContributor
{
    private static readonly IReadOnlyList<string> s_providedSeams = ["mcp"];

    /// <summary>
    /// 静态 ProvidedTools：管理工具 + Resource 工具。并行 Task 追加 id 时请用 Contain 断言，勿互斥 HaveCount。
    /// </summary>
    private static readonly string[] s_providedTools =
    [
        "mcp_list_servers",
        "mcp_get_server",
        "mcp_add_server",
        "mcp_update_server",
        "mcp_remove_server",
        "mcp_enable_server",
        "mcp_disable_server",
        "mcp_pause_server",
        "mcp_resume_server",
        "mcp_reconnect_server",
        "mcp_import_servers",
        "mcp_list_resources",
        "mcp_read_resource",
        "mcp_list_resource_templates",
    ];

    private readonly List<string> _registeredCommandNames = new();
    private McpPromptCommandSync? _promptCommandSync;
    private McpSkillContributionSource? _skillContribution;

    /// <inheritdoc />
    public string Id => "mcp";

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedTools => s_providedTools;

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

        // 同一实例也以接口暴露：McpClientManager 构造参数取 IMcpToolRegistry，
        // 这样 DI 的这两个单例才是真正被使用的实例（历史缺陷：Manager 自行 new，
        // 容器里的注册成了永不使用的死注册）。
        services.AddSingleton<IMcpToolRegistry>(sp => sp.GetRequiredService<McpToolRegistry>());

        services.AddSingleton<McpProcessMonitor>(sp =>
        {
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<McpProcessMonitor>();
            return new McpProcessMonitor(logger);
        });

        services.AddSingleton<IMcpConfigPersistence, McpConfigPersistence>();
        services.AddSingleton<IMcpManager, McpClientManager>();
        services.AddSingleton<McpClientManager>(sp => (McpClientManager)sp.GetRequiredService<IMcpManager>());
        services.AddSingleton<IComponentLoader, McpLoader>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWorkspaceApplyParticipant, McpWorkspaceParticipant>());

        // 管理工具（Activate 时 RegisterToolAsync）
        services.AddSingleton<McpListServersTool>();
        services.AddSingleton<McpGetServerTool>();
        services.AddSingleton<McpAddServerTool>();
        services.AddSingleton<McpUpdateServerTool>();
        services.AddSingleton<McpRemoveServerTool>();
        services.AddSingleton<McpEnableServerTool>();
        services.AddSingleton<McpDisableServerTool>();
        services.AddSingleton<McpPauseServerTool>();
        services.AddSingleton<McpResumeServerTool>();
        services.AddSingleton<McpReconnectServerTool>();
        services.AddSingleton<McpImportServersTool>();

        // Resource 工具
        services.AddSingleton<McpListResourcesTool>();
        services.AddSingleton<McpReadResourceTool>();
        services.AddSingleton<McpListResourceTemplatesTool>();
        // MCP Skills 贡献源（Activate 登记到 ISkillContributionRegistry；不走 RegisterAsync）
        services.AddSingleton<McpSkillContributionSource>(sp =>
        {
            var mgr = sp.GetRequiredService<IMcpManager>();
            var registry = sp.GetService<ISkillContributionRegistry>();
            var logger = sp.GetService<ILogger<McpSkillContributionSource>>();
            var factory = sp.GetService<IPermissionAuthorizerFactory>();
            return new McpSkillContributionSource(
                mgr,
                registry,
                authorizeResourceRead: CreateSkillResourceAuthorizer(factory),
                logger);
        });
    }

    /// <inheritdoc />
    public IReadOnlyList<UiContribution> DescribeUi() =>
    [
        new PageContribution
        {
            Point = "shell.page",
            Id = "page.mcp",
            Route = "/mcp",
            Title = "MCP",
            ViewKey = "page.mcp",
            Requires = [Id],
            Order = 30,
            Nav = new NavPlacement
            {
                Icon = "api",
                Group = NavGroups.Workspace,
                GroupIcon = NavGroups.WorkspaceIcon,
                GroupOrder = 20,
            },
        },
    ];

    /// <inheritdoc />
    public async Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        // 初始化连接（配置加载复用 McpLoader 的静态入口）
        var manager = services.GetService<IMcpManager>();
        if (manager is not null)
        {
            var configs = await LoadConfigsAsync(services, cancellationToken).ConfigureAwait(false);
            await manager.InitializeAsync(configs, cancellationToken).ConfigureAwait(false);
        }

        // 登记 OAuth 授权命令（配置在 InitializeAsync 后才可用）
        RegisterCommands(services);

        await RegisterProvidedToolsAsync(services, cancellationToken).ConfigureAwait(false);
        RegisterSkillContribution(services);

        // 登记动态工具贡献（工具集运行时可变，结算时并入 settledToolIds）
        services.GetService<IDynamicToolContributorRegistry>()
            ?.Register(new McpDynamicToolContributor(manager));
    }

    /// <inheritdoc />
    public async Task DeactivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        UnregisterSkillContribution(services);
        UnregisterCommands(services);
        await UnregisterProvidedToolsAsync(services, cancellationToken).ConfigureAwait(false);

        services.GetService<IDynamicToolContributorRegistry>()?.Unregister(Id);

        var manager = services.GetService<IMcpManager>();
        if (manager is not null)
        {
            await manager.ShutdownAsync(cancellationToken).ConfigureAwait(false);
            await manager.UnregisterAllToolsAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task RegisterProvidedToolsAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var tm = services.GetService<IToolManager>();
        if (tm is null)
            return;

        if (services.GetService<McpListServersTool>() is { } listServers)
            await tm.RegisterToolAsync(listServers, cancellationToken).ConfigureAwait(false);
        if (services.GetService<McpGetServerTool>() is { } getServer)
            await tm.RegisterToolAsync(getServer, cancellationToken).ConfigureAwait(false);
        if (services.GetService<McpAddServerTool>() is { } addServer)
            await tm.RegisterToolAsync(addServer, cancellationToken).ConfigureAwait(false);
        if (services.GetService<McpUpdateServerTool>() is { } updateServer)
            await tm.RegisterToolAsync(updateServer, cancellationToken).ConfigureAwait(false);
        if (services.GetService<McpRemoveServerTool>() is { } removeServer)
            await tm.RegisterToolAsync(removeServer, cancellationToken).ConfigureAwait(false);
        if (services.GetService<McpEnableServerTool>() is { } enableServer)
            await tm.RegisterToolAsync(enableServer, cancellationToken).ConfigureAwait(false);
        if (services.GetService<McpDisableServerTool>() is { } disableServer)
            await tm.RegisterToolAsync(disableServer, cancellationToken).ConfigureAwait(false);
        if (services.GetService<McpPauseServerTool>() is { } pauseServer)
            await tm.RegisterToolAsync(pauseServer, cancellationToken).ConfigureAwait(false);
        if (services.GetService<McpResumeServerTool>() is { } resumeServer)
            await tm.RegisterToolAsync(resumeServer, cancellationToken).ConfigureAwait(false);
        if (services.GetService<McpReconnectServerTool>() is { } reconnectServer)
            await tm.RegisterToolAsync(reconnectServer, cancellationToken).ConfigureAwait(false);
        if (services.GetService<McpImportServersTool>() is { } importServers)
            await tm.RegisterToolAsync(importServers, cancellationToken).ConfigureAwait(false);

        if (services.GetService<McpListResourcesTool>() is { } list)
            await tm.RegisterToolAsync(list, cancellationToken).ConfigureAwait(false);
        if (services.GetService<McpReadResourceTool>() is { } read)
            await tm.RegisterToolAsync(read, cancellationToken).ConfigureAwait(false);
        if (services.GetService<McpListResourceTemplatesTool>() is { } templates)
            await tm.RegisterToolAsync(templates, cancellationToken).ConfigureAwait(false);
    }

    private static async Task UnregisterProvidedToolsAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var tm = services.GetService<IToolManager>();
        if (tm is null)
            return;

        foreach (var id in s_providedTools)
            await tm.UnregisterToolAsync(id, cancellationToken).ConfigureAwait(false);
    }
    private void RegisterSkillContribution(IServiceProvider services)
    {
        var source = services.GetService<McpSkillContributionSource>();
        if (source is null)
            return;

        _skillContribution = source;
        services.GetService<ISkillContributionRegistry>()?.Register(source);
        source.Attach();
    }

    private void UnregisterSkillContribution(IServiceProvider services)
    {
        if (_skillContribution is null)
            return;

        _skillContribution.Detach();
        services.GetService<ISkillContributionRegistry>()?.Unregister(_skillContribution);
        _skillContribution = null;
    }

    /// <summary>
    /// 技能加载回源：kind=<c>mcp.resource.read</c>，记忆键 server|uri。无工厂则 Fail-closed。
    /// </summary>
    private static Func<string, string, CancellationToken, Task<bool>>? CreateSkillResourceAuthorizer(
        IPermissionAuthorizerFactory? factory)
    {
        if (factory is null)
            return null;

        return async (server, uri, ct) =>
        {
            var authorizer = factory.Create("mcp-skill-load");
            var resolution = await McpResourcePermissionGate
                .AuthorizeWithAuthorizerAsync(server, uri, authorizer, ct)
                .ConfigureAwait(false);
            return resolution.Decision == PermissionEffect.Allow;
        };
    }

    /// <summary>
    /// 登记 <c>/mcp-auth</c>（有授权器时）与 MCP Prompt 斜杠命令同步。
    /// Prompt 同步不依赖 OAuth——缺授权器时仍启动。
    /// </summary>
    private void RegisterCommands(IServiceProvider services)
    {
        _registeredCommandNames.Clear();

        if (services.GetService<ICommandRegistry>() is not { } registry)
            return;

        if (services.GetService<IMcpOAuthAuthorizer>() is { } authorizer)
        {
            var command = new McpAuthCommand(authorizer);
            registry.Register(command);
            _registeredCommandNames.Add(command.Metadata.Name);
        }

        StartPromptCommandSync(services, registry);
    }

    private void StartPromptCommandSync(IServiceProvider services, ICommandRegistry registry)
    {
        _promptCommandSync?.Dispose();
        _promptCommandSync = null;

        if (services.GetService<IMcpManager>() is not { } manager)
            return;

        var logger = services.GetService<ILoggerFactory>()?.CreateLogger("McpPromptCommandSync")
            ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        _promptCommandSync = new McpPromptCommandSync(
            manager,
            registry,
            resolveSession: manager.TryGetSession,
            logger);
        _promptCommandSync.Start();
    }

    /// <summary>注销本模块登记的静态命令，并停止 Prompt 同步（撤销动态 prompt 命令）。</summary>
    private void UnregisterCommands(IServiceProvider services)
    {
        _promptCommandSync?.Dispose();
        _promptCommandSync = null;

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
                .Select(t => McpTool.BuildId(t.ServerName, t.Name))
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
