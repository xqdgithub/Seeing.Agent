using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Mcp;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Core.Tools;
using Seeing.Agent.Mcp.Configuration;
using Seeing.Agent.Mcp.Factory;
using Seeing.Agent.Mcp.Policy;
using Seeing.Agent.Mcp.Workspace;
using Xunit;

namespace Seeing.Agent.Mcp.Tests;

/// <summary>
/// Task 4：工作区参与者 — Applying 不 Save；未 resolved 拒项目写入；suppress 闸。
/// </summary>
public class McpWorkspaceParticipantTests
{
    [Fact]
    public async Task AddServer_Project_When_Unresolved_Rejects_Without_Save()
    {
        var persistence = new Mock<IMcpConfigPersistence>();
        var workspace = new Mock<IWorkspaceProvider>();
        workspace.SetupGet(w => w.IsResolved).Returns(false);

        var manager = CreateManager(persistence.Object, workspace.Object);

        var result = await manager.AddServerAsync(
            "proj",
            new McpServerConfig { Command = "npx" },
            ConfigLevel.Project,
            persist: true,
            connect: false,
            cancellationToken: TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        result.Error!.UserMessage.Should().Contain("工作区");
        persistence.Verify(
            p => p.SaveAsync(It.IsAny<ConfigLevel>(), It.IsAny<IReadOnlyDictionary<string, McpServerConfig>>(), It.IsAny<CancellationToken>()),
            Times.Never);
        manager.GetConfig("proj").Should().BeNull();
    }

    [Fact]
    public async Task SaveConfig_Project_When_Suppressed_Does_Not_Write()
    {
        var persistence = new Mock<IMcpConfigPersistence>();
        var workspace = new Mock<IWorkspaceProvider>();
        workspace.SetupGet(w => w.IsResolved).Returns(true);

        var manager = CreateManager(persistence.Object, workspace.Object);
        await manager.AddServerAsync(
            "proj",
            new McpServerConfig { Command = "npx" },
            ConfigLevel.Project,
            persist: false,
            connect: false,
            cancellationToken: TestContext.Current.CancellationToken);

        manager.SetProjectSaveSuppressed(true);

        var act = async () => await manager.SaveConfigAsync(ConfigLevel.Project, TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<InvalidOperationException>();

        persistence.Verify(
            p => p.SaveAsync(ConfigLevel.Project, It.IsAny<IReadOnlyDictionary<string, McpServerConfig>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task OnApplying_Disconnects_Project_Sessions_Without_Save()
    {
        var persistence = new Mock<IMcpConfigPersistence>();
        persistence.Setup(p => p.LoadAsync(ConfigLevel.Project, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, McpServerConfig>());

        var workspace = new Mock<IWorkspaceProvider>();
        workspace.SetupGet(w => w.IsResolved).Returns(true);

        var manager = CreateManager(persistence.Object, workspace.Object);
        await manager.AddServerAsync(
            "user-srv",
            new McpServerConfig { Command = "npx" },
            ConfigLevel.User,
            persist: false,
            connect: false,
            cancellationToken: TestContext.Current.CancellationToken);
        await manager.AddServerAsync(
            "proj-srv",
            new McpServerConfig { Command = "npx" },
            ConfigLevel.Project,
            persist: false,
            connect: false,
            cancellationToken: TestContext.Current.CancellationToken);

        var participant = new McpWorkspaceParticipant(
            manager, persistence.Object, NullLogger<McpWorkspaceParticipant>.Instance);

        await participant.OnApplyingAsync(
            new WorkspaceApplyContext(@"C:\old", @"C:\new"), TestContext.Current.CancellationToken);

        manager.IsProjectSaveSuppressed.Should().BeTrue();
        manager.GetConfig("proj-srv").Should().BeNull("项目级须在 Applying 阶段从内存剔除，避免写到新根");
        manager.GetConfig("user-srv").Should().NotBeNull("用户级跨切换保留");
        persistence.Verify(
            p => p.SaveAsync(It.IsAny<ConfigLevel>(), It.IsAny<IReadOnlyDictionary<string, McpServerConfig>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task OnApplied_Clears_Suppress_And_Loads_Project_Configs()
    {
        var projectConfig = new McpServerConfig { Command = "npx", Name = "new-proj", ConfigLevel = ConfigLevel.Project };
        var persistence = new Mock<IMcpConfigPersistence>();
        persistence.Setup(p => p.LoadAsync(ConfigLevel.Project, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, McpServerConfig> { ["new-proj"] = projectConfig });

        var workspace = new Mock<IWorkspaceProvider>();
        workspace.SetupGet(w => w.IsResolved).Returns(true);

        var manager = CreateManager(persistence.Object, workspace.Object);
        manager.SetProjectSaveSuppressed(true);

        var participant = new McpWorkspaceParticipant(
            manager, persistence.Object, NullLogger<McpWorkspaceParticipant>.Instance);

        await participant.OnAppliedAsync(
            new WorkspaceApplyContext(@"C:\old", @"C:\new"), TestContext.Current.CancellationToken);

        manager.IsProjectSaveSuppressed.Should().BeFalse();
        manager.GetConfig("new-proj").Should().NotBeNull();
        persistence.Verify(p => p.LoadAsync(ConfigLevel.Project, It.IsAny<CancellationToken>()), Times.Once);
        persistence.Verify(
            p => p.SaveAsync(It.IsAny<ConfigLevel>(), It.IsAny<IReadOnlyDictionary<string, McpServerConfig>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task AddServer_Connect_False_Overrides_AutoStartOnAdd()
    {
        var persistence = new Mock<IMcpConfigPersistence>();
        var workspace = new Mock<IWorkspaceProvider>();
        workspace.SetupGet(w => w.IsResolved).Returns(true);

        var manager = CreateManager(
            persistence.Object,
            workspace.Object,
            new McpGlobalPolicy { AutoStartOnAdd = true });

        var result = await manager.AddServerAsync(
            "no-connect",
            new McpServerConfig { Command = "npx" },
            ConfigLevel.User,
            persist: false,
            connect: false,
            cancellationToken: TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        var status = manager.GetStatus("no-connect");
        status!.State.Should().Be(McpConnectionState.Pending,
            "connect=false 须覆盖 AutoStartOnAdd=true，不得后台连接");
    }

    private static McpClientManager CreateManager(
        IMcpConfigPersistence persistence,
        IWorkspaceProvider workspace,
        McpGlobalPolicy? policy = null)
    {
        var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning));
        var hookManager = new HookManager(loggerFactory.CreateLogger<HookManager>());
        var toolManager = new ToolManager(
            loggerFactory.CreateLogger<ToolManager>(),
            hookManager);
        var factoryRegistry = new McpWrapperFactoryRegistry();

        return new McpClientManager(
            loggerFactory.CreateLogger<McpClientManager>(),
            loggerFactory,
            hookManager,
            toolManager,
            factoryRegistry,
            policy ?? new McpGlobalPolicy { AutoStartOnAdd = false },
            persistence,
            workspaceProvider: workspace);
    }
}
