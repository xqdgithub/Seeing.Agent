using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Abstractions.Components;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Mcp;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Skills;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Extensions;
using Seeing.Agent.MCP;
using Seeing.Agent.MCP.Configuration;
using Seeing.Agent.MCP.Factory;
using Seeing.Agent.MCP.Policy;
using Seeing.Agent.Skills;
using Seeing.Agent.Tools;
using Xunit;

namespace Seeing.Agent.Tests.Core;

/// <summary>
/// ComponentManager 三个内置 Loader（Skill/MCP/Plugin）的 ReloadAsync
/// 与 ComponentManager 作为 IReloadHandler 的分发行为测试
/// </summary>
public class ComponentManagerReloadTests
{
    [Fact]
    public async Task ComponentManager_工作区切换触发三Loader重载()
    {
        // Arrange
        using var workspace = new TempWorkspace();
        var componentManager = CreateComponentManager(workspace.Root, out var services);
        var skillLoader = RegisterRecordingLoader(componentManager, "Skill");
        var mcpLoader = RegisterRecordingLoader(componentManager, "Mcp");
        var pluginLoader = RegisterRecordingLoader(componentManager, "Plugin");

        // 首次加载成功后，重载应走各 Loader 的 ReloadAsync
        await componentManager.LoadAllAsync(workspace.Root);

        // Act
        await componentManager.ReloadAsync(new WorkspaceChange
        {
            OldWorkspace = workspace.Root,
            NewWorkspace = workspace.Root
        });

        // Assert
        skillLoader.ReloadCalls.Should().Be(1);
        mcpLoader.ReloadCalls.Should().Be(1);
        pluginLoader.ReloadCalls.Should().Be(1);
    }

    [Fact]
    public async Task ComponentManager_配置变更空节触发全量重载()
    {
        // Arrange
        using var workspace = new TempWorkspace();
        var componentManager = CreateComponentManager(workspace.Root, out _);
        var skillLoader = RegisterRecordingLoader(componentManager, "Skill");
        var mcpLoader = RegisterRecordingLoader(componentManager, "Mcp");
        var pluginLoader = RegisterRecordingLoader(componentManager, "Plugin");
        await componentManager.LoadAllAsync(workspace.Root);

        // Act
        await componentManager.ReloadAsync(new ConfigChange());

        // Assert
        skillLoader.ReloadCalls.Should().Be(1);
        mcpLoader.ReloadCalls.Should().Be(1);
        pluginLoader.ReloadCalls.Should().Be(1);
    }

    [Fact]
    public async Task ComponentManager_配置变更Skills只重载技能Loader()
    {
        // Arrange
        using var workspace = new TempWorkspace();
        var componentManager = CreateComponentManager(workspace.Root, out _);
        var skillLoader = RegisterRecordingLoader(componentManager, "Skill");
        var mcpLoader = RegisterRecordingLoader(componentManager, "Mcp");
        var pluginLoader = RegisterRecordingLoader(componentManager, "Plugin");
        await componentManager.LoadAllAsync(workspace.Root);

        // Act
        await componentManager.ReloadAsync(new ConfigChange { ChangedSections = new[] { "Skills" } });

        // Assert
        skillLoader.ReloadCalls.Should().Be(1);
        mcpLoader.ReloadCalls.Should().Be(0);
        pluginLoader.ReloadCalls.Should().Be(0);
    }

    [Fact]
    public async Task ComponentManager_配置变更Mcp只重载MCP_Loader()
    {
        // Arrange
        using var workspace = new TempWorkspace();
        var componentManager = CreateComponentManager(workspace.Root, out _);
        var skillLoader = RegisterRecordingLoader(componentManager, "Skill");
        var mcpLoader = RegisterRecordingLoader(componentManager, "Mcp");
        var pluginLoader = RegisterRecordingLoader(componentManager, "Plugin");
        await componentManager.LoadAllAsync(workspace.Root);

        // Act
        await componentManager.ReloadAsync(new ConfigChange { ChangedSections = new[] { "Mcp" } });

        // Assert
        skillLoader.ReloadCalls.Should().Be(0);
        mcpLoader.ReloadCalls.Should().Be(1);
        pluginLoader.ReloadCalls.Should().Be(0);
    }

    [Theory]
    [InlineData("Plugins")]
    [InlineData("PluginEnabled")]
    public async Task ComponentManager_配置变更Plugins节只重载插件Loader(string section)
    {
        // Arrange
        using var workspace = new TempWorkspace();
        var componentManager = CreateComponentManager(workspace.Root, out _);
        var skillLoader = RegisterRecordingLoader(componentManager, "Skill");
        var mcpLoader = RegisterRecordingLoader(componentManager, "Mcp");
        var pluginLoader = RegisterRecordingLoader(componentManager, "Plugin");
        await componentManager.LoadAllAsync(workspace.Root);

        // Act
        await componentManager.ReloadAsync(new ConfigChange { ChangedSections = new[] { section } });

        // Assert
        skillLoader.ReloadCalls.Should().Be(0);
        mcpLoader.ReloadCalls.Should().Be(0);
        pluginLoader.ReloadCalls.Should().Be(1);
    }

    [Fact]
    public async Task ComponentManager_首次加载走LoadAsync已加载后走ReloadAsync()
    {
        // Arrange
        using var workspace = new TempWorkspace();
        var componentManager = CreateComponentManager(workspace.Root, out _);
        var skillLoader = RegisterRecordingLoader(componentManager, "Skill");

        // Act & Assert：首次加载走 LoadAsync
        await componentManager.LoadAllAsync(workspace.Root);
        skillLoader.LoadCalls.Should().Be(1);
        skillLoader.ReloadCalls.Should().Be(0);

        // 第二次全量加载（重载场景）走 ReloadAsync
        await componentManager.LoadAllAsync(workspace.Root);
        skillLoader.LoadCalls.Should().Be(1);
        skillLoader.ReloadCalls.Should().Be(1);
    }

    [Fact]
    public async Task SkillLoader_重载清理已删除技能并重新发现()
    {
        // Arrange
        using var workspace = new TempWorkspace();
        var skillsDir = Path.Combine(workspace.Root, "skills");
        var alphaName = $"task19alpha{workspace.Suffix}";
        var betaName = $"task19beta{workspace.Suffix}";
        var alphaDir = Path.Combine(skillsDir, alphaName);
        Directory.CreateDirectory(alphaDir);
        await File.WriteAllTextAsync(Path.Combine(alphaDir, "SKILL.md"), "技能 Alpha 内容");

        var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning));
        var skillManager = new SkillManager(loggerFactory.CreateLogger<SkillManager>());
        var options = Options.Create(new SeeingAgentOptions
        {
            Skills = new SkillsConfig { Paths = new List<string> { skillsDir } }
        });
        var services = new ServiceCollection()
            .AddSingleton(skillManager)
            .AddSingleton<IOptions<SeeingAgentOptions>>(options)
            .AddSingleton<ILoggerFactory>(loggerFactory)
            .AddSingleton<IWorkspaceProvider>(new WorkspaceProvider(workspace.Root))
            .BuildServiceProvider();

        var loader = new SkillLoader();

        // Act 1：首次加载发现 alpha
        var first = await loader.LoadAsync(services, workspace.Root);
        first.Success.Should().BeTrue();
        first.Details.Should().Contain(alphaName);
        skillManager.GetAllSkillInfos().Should().ContainKey(alphaName);

        // Act 2：新增 beta 后重载，两个技能都应被发现
        var betaDir = Path.Combine(skillsDir, betaName);
        Directory.CreateDirectory(betaDir);
        await File.WriteAllTextAsync(Path.Combine(betaDir, "SKILL.md"), "技能 Beta 内容");

        var reload = await loader.ReloadAsync(services, workspace.Root);
        reload.Success.Should().BeTrue();
        reload.Details.Should().Contain(alphaName).And.Contain(betaName);
        skillManager.GetAllSkillInfos().Should().ContainKey(betaName);

        // Act 3：删除 beta 后重载，旧技能信息应被清理（区别于默认 ReloadAsync 转调 LoadAsync 的关键语义）
        Directory.Delete(betaDir, true);
        var reload2 = await loader.ReloadAsync(services, workspace.Root);
        reload2.Success.Should().BeTrue();
        skillManager.GetAllSkillInfos().Should().NotContainKey(betaName);
        skillManager.GetAllSkillInfos().Should().ContainKey(alphaName);
    }

    [Fact]
    public async Task McpLoader_重载重置连接后按最新配置重新初始化()
    {
        // Arrange
        using var workspace = new TempWorkspace();
        var seeingDir = Path.Combine(workspace.Root, ".seeing");
        Directory.CreateDirectory(seeingDir);
        var mcpConfigPath = Path.Combine(seeingDir, "mcp.json");
        var serverA = $"task19mcpa{workspace.Suffix}";
        var serverB = $"task19mcpb{workspace.Suffix}";
        await File.WriteAllTextAsync(mcpConfigPath, $$"""
        {
            "mcpServers": {
                "{{serverA}}": { "command": "npx", "disabled": true }
            }
        }
        """);

        var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning));
        var mcpManager = CreateMcpManager(loggerFactory);
        var workspaceProvider = new WorkspaceProvider(workspace.Root);
        var services = new ServiceCollection()
            .AddSingleton(mcpManager)
            .AddSingleton<ILoggerFactory>(loggerFactory)
            .AddSingleton<IWorkspaceProvider>(workspaceProvider)
            .BuildServiceProvider();

        var loader = new McpLoader();

        try
        {
            // Act 1：首次加载
            var first = await loader.LoadAsync(services, workspace.Root);
            first.Success.Should().BeTrue();
            mcpManager.GetConfig(serverA).Should().NotBeNull();

            // 新增服务器配置后重载，应重置连接并按最新配置重新初始化
            await File.WriteAllTextAsync(mcpConfigPath, $$"""
            {
                "mcpServers": {
                    "{{serverA}}": { "command": "npx", "disabled": true },
                    "{{serverB}}": { "command": "npx", "disabled": true }
                }
            }
            """);

            // Act 2：重载
            var reload = await loader.ReloadAsync(services, workspace.Root);
            reload.Success.Should().BeTrue();
            reload.Count.Should().BeGreaterThanOrEqualTo(2);
            mcpManager.GetConfig(serverB).Should().NotBeNull();
            mcpManager.GetConfig(serverA).Should().NotBeNull();
        }
        finally
        {
            await mcpManager.ShutdownAsync();
        }
    }

    [Fact]
    public async Task PluginLoader_重载成功无插件配置()
    {
        // Arrange
        using var workspace = new TempWorkspace();
        using var provider = CreatePluginLoaderServices(workspace.Root);
        var loader = new PluginLoader();

        // Act：无插件配置时重载不应抛异常，且返回成功
        var result = await loader.ReloadAsync(provider, workspace.Root);

        // Assert
        result.Success.Should().BeTrue();
        result.Error.Should().BeNull();
    }

    private static ComponentManager CreateComponentManager(string workspaceRoot, out ServiceProvider services)
    {
        var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning));
        services = new ServiceCollection()
            .AddSingleton<IWorkspaceProvider>(new WorkspaceProvider(workspaceRoot))
            .BuildServiceProvider();
        return new ComponentManager(services, loggerFactory.CreateLogger<ComponentManager>());
    }

    private static RecordingLoader RegisterRecordingLoader(ComponentManager manager, string type)
    {
        var loader = new RecordingLoader(type);
        manager.RegisterLoader(loader);
        return loader;
    }

    private static ServiceProvider CreatePluginLoaderServices(string workspaceRoot)
    {
        var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning));
        var hookManager = new HookManager(loggerFactory.CreateLogger<HookManager>());
        var toolManager = new ToolManager(loggerFactory.CreateLogger<ToolManager>(), hookManager);
        var mcpManager = CreateMcpManager(loggerFactory);

        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(loggerFactory);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(Options.Create(new SeeingAgentOptions()));
        services.AddSingleton<IWorkspaceProvider>(new WorkspaceProvider(workspaceRoot));
        services.AddSingleton(hookManager);
        services.AddSingleton(toolManager);
        services.AddSingleton(mcpManager);
        services.AddSingleton(new ExtensionManager(
            loggerFactory.CreateLogger<ExtensionManager>(),
            new ExtensionLoader(loggerFactory.CreateLogger<ExtensionLoader>())));
        services.AddSingleton<IPermissionService>(new Mock<IPermissionService>().Object);
        services.AddSingleton<IAgentRegistry>(new Mock<IAgentRegistry>().Object);
        services.AddSingleton<ISkillManager>(new Mock<ISkillManager>().Object);
        services.AddSingleton<ICommandRegistry>(new Mock<ICommandRegistry>().Object);
        return services.BuildServiceProvider();
    }

    private static McpClientManager CreateMcpManager(ILoggerFactory loggerFactory)
    {
        var hookManager = new HookManager(loggerFactory.CreateLogger<HookManager>());
        var toolManager = new ToolManager(loggerFactory.CreateLogger<ToolManager>(), hookManager);
        var factoryRegistry = new McpWrapperFactoryRegistry();
        factoryRegistry.Register(new StdioWrapperFactory());
        var configPersistence = new Mock<IMcpConfigPersistence>().Object;
        return new McpClientManager(
            loggerFactory.CreateLogger<McpClientManager>(),
            loggerFactory,
            hookManager,
            toolManager,
            factoryRegistry,
            new McpGlobalPolicy(),
            configPersistence);
    }

    /// <summary>记录 Load/Reload 调用次数的测试加载器</summary>
    private sealed class RecordingLoader : IComponentLoader
    {
        public RecordingLoader(string type)
        {
            Type = type;
        }

        public string Type { get; }

        public int LoadCalls { get; private set; }

        public int ReloadCalls { get; private set; }

        public Task<ComponentLoadResult> LoadAsync(
            IServiceProvider services,
            string workspaceRoot,
            CancellationToken cancellationToken = default)
        {
            LoadCalls++;
            return Task.FromResult(new ComponentLoadResult { Type = Type, Success = true, Count = 1 });
        }

        public Task<ComponentLoadResult> ReloadAsync(
            IServiceProvider services,
            string workspaceRoot,
            CancellationToken cancellationToken = default)
        {
            ReloadCalls++;
            return Task.FromResult(new ComponentLoadResult { Type = Type, Success = true, Count = 1 });
        }
    }

    /// <summary>临时工作区目录</summary>
    private sealed class TempWorkspace : IDisposable
    {
        public TempWorkspace()
        {
            Root = Path.Combine(Path.GetTempPath(), "seeing-agent-test-" + Guid.NewGuid().ToString("N"));
            Suffix = Guid.NewGuid().ToString("N")[..6];
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string Suffix { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, true);
            }
            catch (IOException)
            {
                // 忽略清理时的文件锁竞争
            }
        }
    }
}
