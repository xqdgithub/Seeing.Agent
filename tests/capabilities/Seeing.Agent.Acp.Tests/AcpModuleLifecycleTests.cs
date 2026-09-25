using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Abstractions.Configuration;
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
    public async Task Activate_注册命令_Deactivate_注销()
    {
        var registry = new CommandRegistry();
        var discovery = new Mock<ICommandDiscovery>();
        discovery.Setup(d => d.DiscoverFromType(It.IsAny<Type>(), It.IsAny<object?>()))
            .Returns(Array.Empty<ICommand>());

        var skillManager = new Mock<ISkillManager>();
        skillManager.Setup(m => m.GetAllSkillInfos()).Returns(new Dictionary<string, SkillInfo>
        {
            ["demo-skill"] = new() { Name = "demo-skill", Description = "演示" }
        });

        var options = CreateOptions(new AcpOptions());
        var services = new ServiceCollection();
        services.AddSingleton<ICommandRegistry>(registry);
        services.AddSingleton(discovery.Object);
        services.AddSingleton(skillManager.Object);
        services.AddSingleton(new AcpCommands());
        await using var provider = services.BuildServiceProvider();

        var module = CreateModule(options);
        await module.ActivateAsync(provider);

        registry.HasCommand("demo-skill").Should().BeTrue();

        await module.DeactivateAsync(provider);

        registry.HasCommand("demo-skill").Should().BeFalse();
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
