using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Skills;
using Seeing.Agent.Acp.Backends;
using Seeing.Agent.Acp.Client;
using Seeing.Agent.Acp.Commands;
using Seeing.Agent.Acp.Configuration;
using Seeing.Agent.Acp.Filesystem;
using Seeing.Agent.Acp.Hosting;
using Seeing.Agent.Acp.Permission;
using Seeing.Agent.Acp.Session;
using Seeing.Agent.Acp.Terminal;
using Seeing.Agent.Acp.Transport;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Commands;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Skills.Catalog;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Acp.Tests;

/// <summary>ACP 模块生命周期收编：Agent / ReloadHandler / 命令注册与撤销。</summary>
public class AcpModuleLifecycleTests
{
    [Fact]
    public async Task Activate_注册透传Agent_Deactivate_注销()
    {
        var agents = new List<AgentDefinition>();
        var agentRegistry = new Mock<IAgentRegistry>();
        agentRegistry.Setup(r => r.HasAgent(It.IsAny<string>())).Returns(false);
        agentRegistry.Setup(r => r.GetAgentsAsync()).ReturnsAsync(() => agents.ToList());
        agentRegistry.Setup(r => r.RegisterAgentAsync(It.IsAny<AgentDefinition>()))
            .Callback<AgentDefinition>(agents.Add)
            .Returns(Task.CompletedTask);
        agentRegistry.Setup(r => r.UnregisterAgent(It.IsAny<string>()))
            .Callback<string>(n => agents.RemoveAll(a => a.Name == n))
            .Returns(true);

        var options = CreateOptions(new AcpOptions
        {
            Enabled = true,
            Backends = new Dictionary<string, AcpBackendConfig>
            {
                ["opencode"] = new() { Command = "opencode" }
            }
        });

        var services = new ServiceCollection();
        services.AddSingleton(agentRegistry.Object);
        services.AddSingleton<IAcpBackendRegistry>(
            new AcpBackendRegistry(options, NullLogger<AcpBackendRegistry>.Instance));
        services.AddSingleton(options);
        await using var provider = services.BuildServiceProvider();

        var module = CreateModule(options);

        await module.ActivateAsync(provider);
        agents.Select(a => a.Name).Should().Contain("acp-opencode");

        await module.DeactivateAsync(provider);
        agentRegistry.Verify(r => r.UnregisterAgent("acp-opencode"), Times.Once);
        agents.Should().NotContain(a => a.Name == "acp-opencode");
    }

    [Fact]
    public async Task Activate_挂接ReloadHandler_Deactivate_撤销_配置重载不复活()
    {
        var reloader = new Mock<IAcpConfigurationReloader>();
        var orchestrator = new ReloadOrchestrator(
            Mock.Of<IConfigSectionStore>(),
            Mock.Of<IWorkspaceProvider>(),
            NullLogger<ReloadOrchestrator>.Instance);

        var options = CreateOptions(new AcpOptions());
        var services = new ServiceCollection();
        services.AddSingleton<IReloadHandlerRegistry>(orchestrator);
        services.AddSingleton(new AcpReloadHandler(reloader.Object));
        await using var provider = services.BuildServiceProvider();

        var module = CreateModule(options);
        await module.ActivateAsync(provider);

        await orchestrator.PublishAsync(new ConfigChange { ChangedSections = new[] { "Acp" } });
        reloader.Verify(r => r.ReloadAsync(It.IsAny<CancellationToken>()), Times.Once);

        await module.DeactivateAsync(provider);

        await orchestrator.PublishAsync(new ConfigChange { ChangedSections = new[] { "Acp" } });
        reloader.Verify(r => r.ReloadAsync(It.IsAny<CancellationToken>()), Times.Once,
            "停用后 AcpReloadHandler 已撤销，配置重载不得再触发");
    }

    [Fact]
    public async Task AcpCommands_ShouldFollowCatalogEvents()
    {
        var registry = new CommandRegistry();
        var discovery = new Mock<ICommandDiscovery>();
        discovery.Setup(d => d.DiscoverFromType(It.IsAny<Type>(), It.IsAny<object?>()))
            .Returns(Array.Empty<ICommand>());

        var catalog = new SkillCatalogCore(NullLogger<SkillCatalogCore>.Instance);
        catalog.SetRoots([Path.Combine(Path.GetTempPath(), $"seeing-acp-skill-{Guid.NewGuid():N}")]);
        await catalog.RegisterAsync(
            new SkillInfo { Name = "demo-skill", Description = "演示" },
            "test",
            TestContext.Current.CancellationToken);

        var options = CreateOptions(new AcpOptions());
        var services = new ServiceCollection();
        services.AddSingleton(catalog);
        services.AddSingleton<ISkillCatalog>(catalog);
        services.AddSingleton<ICommandRegistry>(registry);
        services.AddSingleton(discovery.Object);
        services.AddSingleton(new AcpCommands());
        await using var provider = services.BuildServiceProvider();

        var module = CreateModule(options);
        await module.ActivateAsync(provider);

        registry.HasCommand("demo-skill").Should().BeTrue("激活时应按目录快照登记 ACP 动态技能命令");

        // 目录事件：删除后命令必须撤销
        (await catalog.UnregisterAsync("demo-skill", TestContext.Current.CancellationToken))
            .Should().BeTrue();
        registry.HasCommand("demo-skill").Should().BeFalse("目录变化必须驱动命令同步");

        // 新技能出现后命令随即登记
        await catalog.RegisterAsync(
            new SkillInfo { Name = "later-skill", Description = "后来的" },
            "test",
            TestContext.Current.CancellationToken);
        registry.HasCommand("later-skill").Should().BeTrue();

        await module.DeactivateAsync(provider);

        registry.HasCommand("later-skill").Should().BeFalse();
    }

    [Fact]
    public async Task Activate_登记SessionLifecycleHook_Deactivate_撤销()
    {
        var options = CreateOptions(new AcpOptions());
        var activity = new AcpModuleActivity();
        var owner = new AcpConnectionOwner(() => CreateManager(options));
        var terminal = new AcpTerminalBridge(NullLogger<AcpTerminalBridge>.Instance);
        var sessionStore = new AcpSessionStore(Mock.Of<ISessionManager>(), NullLogger<AcpSessionStore>.Instance);
        var lifecycleHook = new AcpSessionLifecycleHook(
            sessionStore, owner, terminal, NullLogger<AcpSessionLifecycleHook>.Instance);
        var hookManager = new HookManager(NullLogger<HookManager>.Instance);

        var services = new ServiceCollection();
        services.AddSingleton<IHookManager>(hookManager);
        services.AddSingleton(lifecycleHook);
        await using var provider = services.BuildServiceProvider();

        var module = new AcpModule(activity, owner);

        await module.ActivateAsync(provider, TestContext.Current.CancellationToken);
        foreach (var spec in lifecycleHook.Specs)
            hookManager.Count(spec).Should().Be(1, $"Activate 后应登记 {spec.Point}");

        await module.DeactivateAsync(provider, TestContext.Current.CancellationToken);
        foreach (var spec in lifecycleHook.Specs)
            hookManager.Count(spec).Should().Be(0, $"Deactivate 后应撤销 {spec.Point}");
    }

    [Fact]
    public async Task SkillCommandSync_LateEventAfterDeactivate_ShouldNotReregister()
    {
        // I2：Deactivate 之后（订阅已失效 / 事件在途）的目录事件不得把命令重新登记回来
        var registry = new BlockingCommandRegistry();
        var discovery = new Mock<ICommandDiscovery>();
        discovery.Setup(d => d.DiscoverFromType(It.IsAny<Type>(), It.IsAny<object?>()))
            .Returns(Array.Empty<ICommand>());

        var catalog = new FakeSkillCatalog();
        var services = new ServiceCollection();
        services.AddSingleton<ISkillCatalog>(catalog);
        services.AddSingleton<ICommandRegistry>(registry);
        services.AddSingleton(discovery.Object);
        await using var provider = services.BuildServiceProvider();

        var module = CreateModule(CreateOptions(new AcpOptions()));
        await module.ActivateAsync(provider, TestContext.Current.CancellationToken);

        await module.DeactivateAsync(provider, TestContext.Current.CancellationToken);
        // 幂等：重复停用不得抛
        await module.DeactivateAsync(provider, TestContext.Current.CancellationToken);

        // 晚到事件（模拟已进入回调队列的目录刷新）
        catalog.Raise(Snapshot("late-skill"));

        registry.HasCommand("late-skill").Should().BeFalse("停用后晚到的目录事件不得复活 ACP 技能命令");
        registry.RegistrationAttempts.Should().Be(0);
    }

    [Fact]
    public async Task SkillCommandSync_ConcurrentCatalogEvents_ShouldRegisterOnce()
    {
        // I2：两个目录事件并发投递时，集合读写必须在同一把锁内（否则同一命令登记两次）
        var registry = new BlockingCommandRegistry { BlockFirstRegistration = true };
        var discovery = new Mock<ICommandDiscovery>();
        discovery.Setup(d => d.DiscoverFromType(It.IsAny<Type>(), It.IsAny<object?>()))
            .Returns(Array.Empty<ICommand>());

        var catalog = new FakeSkillCatalog();
        var services = new ServiceCollection();
        services.AddSingleton<ISkillCatalog>(catalog);
        services.AddSingleton<ICommandRegistry>(registry);
        services.AddSingleton(discovery.Object);
        await using var provider = services.BuildServiceProvider();

        var module = CreateModule(CreateOptions(new AcpOptions()));
        await module.ActivateAsync(provider, TestContext.Current.CancellationToken);

        var snapshot = Snapshot("race-skill");
        var first = Task.Run(() => catalog.Raise(snapshot), TestContext.Current.CancellationToken);

        // 等第一个回调进入 Register 内部（此时它已持有同步闸）
        registry.FirstRegistrationEntered.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue(
            "第一个目录事件应已进入命令登记");

        // 第二个并发事件：有锁时必须等待；随后释放第一个，两者共只登记一次
        var second = Task.Run(() => catalog.Raise(snapshot), TestContext.Current.CancellationToken);
        await Task.Delay(150, TestContext.Current.CancellationToken);
        registry.ReleaseFirstRegistration();

        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        registry.RegistrationAttempts.Should().Be(1, "并发目录事件不得重复登记同一命令");
        registry.HasCommand("race-skill").Should().BeTrue();

        await module.DeactivateAsync(provider, TestContext.Current.CancellationToken);
        registry.HasCommand("race-skill").Should().BeFalse();
    }

    private static SkillCatalogSnapshot Snapshot(params string[] names) =>
        new(
            1,
            names.Select(name => new SkillCatalogEntry(
                new SkillInfo { Name = name, Description = $"{name} 技能" },
                SkillSource.Registered,
                "test",
                string.Empty)).ToArray(),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, ConfigLevel>(StringComparer.OrdinalIgnoreCase),
            [],
            []);

    /// <summary>脚本化目录：事件由测试显式触发（可模拟晚到 / 并发投递）。</summary>
    private sealed class FakeSkillCatalog : ISkillCatalog
    {
        private readonly List<Action<SkillCatalogSnapshot>> _handlers = [];

        public SkillCatalogSnapshot Current { get; private set; } =
            new(0, [], new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, ConfigLevel>(StringComparer.OrdinalIgnoreCase), [], []);

        public IReadOnlyList<string> Roots => [];

        public IReadOnlyList<SkillCatalogEntry> EnabledEntries => Current.Entries;

        public IReadOnlyList<SkillCatalogEntry> GetEnabledEntries(SkillCatalogSnapshot snapshot) => snapshot.Entries;

        public IDisposable Subscribe(Action<SkillCatalogSnapshot> onChanged)
        {
            lock (_handlers)
                _handlers.Add(onChanged);

            // 故意不在 Dispose 时移除：模拟「事件已进入回调队列」的晚到投递
            return new NoopDisposable();
        }

        public void Raise(SkillCatalogSnapshot snapshot)
        {
            Current = snapshot;

            Action<SkillCatalogSnapshot>[] handlers;
            lock (_handlers)
                handlers = _handlers.ToArray();

            foreach (var handler in handlers)
                handler(snapshot);
        }

        public SkillCatalogEntry? Find(string name) => null;

        public bool IsEnabled(string name) => false;

        public Task<SkillCatalogSnapshot> EnsureFreshAsync(CancellationToken ct = default) =>
            Task.FromResult(Current);

        public Task<SkillCatalogSnapshot> RefreshAsync(CancellationToken ct = default) =>
            Task.FromResult(Current);

        public Task<bool> RegisterAsync(SkillInfo info, string providerId, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<bool> UnregisterAsync(string name, CancellationToken ct = default) =>
            Task.FromResult(false);

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    /// <summary>可阻塞首次登记的命令注册表：用于制造确定性的双线程交错。</summary>
    private sealed class BlockingCommandRegistry : ICommandRegistry
    {
        private readonly CommandRegistry _inner = new();
        private readonly ManualResetEventSlim _firstRegistrationEntered = new(false);
        private readonly ManualResetEventSlim _releaseFirstRegistration = new(false);
        private int _blockFirst;
        private int _registrationAttempts;

        public bool BlockFirstRegistration { get; init; }

        public ManualResetEventSlim FirstRegistrationEntered => _firstRegistrationEntered;

        public int RegistrationAttempts => Volatile.Read(ref _registrationAttempts);

        public void ReleaseFirstRegistration() => _releaseFirstRegistration.Set();

        public void Register(ICommand command)
        {
            Interlocked.Increment(ref _registrationAttempts);

            if (BlockFirstRegistration && Interlocked.Exchange(ref _blockFirst, 1) == 0)
            {
                _firstRegistrationEntered.Set();
                _releaseFirstRegistration.Wait(TimeSpan.FromSeconds(10));
            }

            _inner.Register(command);
        }

        public void RegisterAll(IEnumerable<ICommand> commands)
        {
            foreach (var command in commands)
                Register(command);
        }

        public ICommand? GetCommand(string name) => _inner.GetCommand(name);

        public ICommand? GetCommand(string name, AgentRuntime runtime) => _inner.GetCommand(name, runtime);

        public IEnumerable<ICommand> GetAllCommands() => _inner.GetAllCommands();

        public IEnumerable<ICommand> GetCommandsByRuntime(AgentRuntime runtime) => _inner.GetCommandsByRuntime(runtime);

        public IEnumerable<ICommand> GetCommandsByCategory(CommandCategory category) =>
            _inner.GetCommandsByCategory(category);

        public IEnumerable<CommandMetadata> GetAllMetadata() => _inner.GetAllMetadata();

        public bool HasCommand(string name) => _inner.HasCommand(name);

        public bool Unregister(string name) => _inner.Unregister(name);

        public int Count => _inner.Count;
    }

    private static IOptionsMonitor<AcpOptions> CreateOptions(AcpOptions value)
    {
        var monitor = new Mock<IOptionsMonitor<AcpOptions>>();
        monitor.Setup(x => x.CurrentValue).Returns(value);
        return monitor.Object;
    }

    private static AcpModule CreateModule(IOptionsMonitor<AcpOptions> options)
    {
        var activity = new AcpModuleActivity();
        var owner = new AcpConnectionOwner(() => CreateManager(options));
        return new AcpModule(activity, owner);
    }

    private static AcpConnectionManager CreateManager(IOptionsMonitor<AcpOptions> options)
    {
        var permission = new AcpPermissionBridge(
            Mock.Of<IPermissionAuthorizerFactory>(),
            NullLogger<AcpPermissionBridge>.Instance);
        var fs = new AcpFileSystemBridge(NullLogger<AcpFileSystemBridge>.Instance);
        var terminal = new AcpTerminalBridge(NullLogger<AcpTerminalBridge>.Instance);
        var backends = new Mock<IAcpBackendRegistry>();
        var clientFactory = new SeeingAcpClientFactory(
            backends.Object,
            permission,
            fs,
            terminal,
            options,
            NullLoggerFactory.Instance);

        var sessions = new Mock<ISessionManager>();
        var store = new AcpSessionStore(sessions.Object, NullLogger<AcpSessionStore>.Instance);

        return new AcpConnectionManager(
            clientFactory,
            store,
            options,
            NullLogger<AcpConnectionManager>.Instance);
    }
}
