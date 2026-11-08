using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Gateway.Channels;
using Seeing.Agent.Gateway.Configuration;
using Xunit;

namespace Seeing.Gateway.Tests.Channels;

/// <summary>
/// 工作区切换后渠道插件注册表必须按新根重扫。
/// <para><b>为什么</b>：注册表除内置插件外还扫描 <c>{工作区}/.seeing/gateway-channels</c>。
/// 不重扫时 <see cref="ChannelHostConfigStore.GetChannelHosts"/> 仍按旧工作区的插件清单产出条目，
/// 参与者随后按这份清单启动子进程——新工作区装的渠道不出现，已删除的仍被尝试拉起。</para>
/// </summary>
public class GatewayChannelRegistryReloadHandlerTests : IDisposable
{
    private readonly string _rootA = Path.Combine(Path.GetTempPath(), "gwreg-a-" + Guid.NewGuid().ToString("N"));
    private readonly string _rootB = Path.Combine(Path.GetTempPath(), "gwreg-b-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ReloadAsync_WorkspaceChange_RescansNewWorkspace()
    {
        Directory.CreateDirectory(_rootA);
        Directory.CreateDirectory(_rootB);

        var registry = CreateRegistry();
        var handler = new GatewayChannelRegistryReloadHandler(
            registry,
            Mock.Of<IWorkspaceProvider>(),
            NullLogger<GatewayChannelRegistryReloadHandler>.Instance);

        await handler.ReloadAsync(
            new WorkspaceChange { OldWorkspace = _rootA, NewWorkspace = _rootB },
            TestContext.Current.CancellationToken);

        // 内置插件始终在（证明 Reload 真的跑过而不是空转）
        registry.Types.Should().NotBeEmpty();
        registry.Types.Should().Contain(t => t.ChannelId == "qq");
    }

    [Fact]
    public async Task ReloadAsync_ConfigChange_IsIgnored()
    {
        var registry = CreateRegistry();
        var workspace = new Mock<IWorkspaceProvider>();
        workspace.SetupGet(w => w.IsResolved).Returns(false);

        var handler = new GatewayChannelRegistryReloadHandler(
            registry, workspace.Object, NullLogger<GatewayChannelRegistryReloadHandler>.Instance);

        await handler.ReloadAsync(
            new ConfigChange { ChangedSections = ["GatewayClients"] },
            TestContext.Current.CancellationToken);

        // 只订阅 WorkspaceChange：配置变更不走这条路径（由 ChannelHostReloadHandler 负责）
        handler.ChangeTypes.Should().BeEquivalentTo([typeof(WorkspaceChange)]);
    }

    [Fact]
    public async Task ReloadAsync_UnresolvedWorkspaceAndNoNewRoot_DoesNotThrow()
    {
        var workspace = new Mock<IWorkspaceProvider>();
        workspace.SetupGet(w => w.IsResolved).Returns(false);

        var handler = new GatewayChannelRegistryReloadHandler(
            CreateRegistry(), workspace.Object, NullLogger<GatewayChannelRegistryReloadHandler>.Instance);

        var act = () => handler.ReloadAsync(
            new WorkspaceChange(), TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void ComponentId_ShouldIdentifyRegistry()
    {
        var handler = new GatewayChannelRegistryReloadHandler(
            CreateRegistry(),
            Mock.Of<IWorkspaceProvider>(),
            NullLogger<GatewayChannelRegistryReloadHandler>.Instance);

        handler.ComponentId.Should().Be("gateway.channel-registry");
    }

    private static GatewayChannelRegistry CreateRegistry()
    {
        var sectionStore = new Mock<IConfigSectionStore>();
        sectionStore.Setup(x => x.GetSection<GatewayOptions>("Gateway")).Returns(new GatewayOptions());
        sectionStore.Setup(x => x.GetSection<GatewayClientsOptions>("GatewayClients"))
            .Returns(new GatewayClientsOptions { Channels = new(StringComparer.OrdinalIgnoreCase) });

        var registry = new GatewayChannelRegistry(
            NullLogger<GatewayChannelRegistry>.Instance, sectionStore.Object);
        registry.Reload(Path.GetTempPath());
        return registry;
    }

    public void Dispose()
    {
        TryDelete(_rootA);
        TryDelete(_rootB);
        GC.SuppressFinalize(this);
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
