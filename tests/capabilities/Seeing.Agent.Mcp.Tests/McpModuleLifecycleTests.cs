using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Seeing.Agent.Abstractions.Mcp;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Abstractions.Ui;
using Xunit;

namespace Seeing.Agent.Mcp.Tests;

/// <summary>
/// McpModule 生命周期（Activate 初始化连接 + 登记动态工具贡献；Deactivate 关闭 + 卸载工具 + 撤销贡献）
/// 与 McpLoader 模块归属，以及 UI 声明（<see cref="IUiContributor"/>）的形状。
/// </summary>
public class McpModuleLifecycleTests
{
    [Fact]
    public void McpLoader_ModuleId_IsMcp()
    {
        new McpLoader().ModuleId.Should().Be("mcp");
    }

    [Fact]
    public async Task Activate_InitializesManager_AndRegistersDynamicContributor()
    {
        IDynamicToolContributor? captured = null;
        var registry = new Mock<IDynamicToolContributorRegistry>();
        registry.Setup(r => r.Register(It.IsAny<IDynamicToolContributor>()))
            .Callback<IDynamicToolContributor>(c => captured = c);

        var manager = new Mock<IMcpManager>();
        manager.Setup(m => m.GetTools()).Returns(new[]
        {
            new McpToolInfo { Name = "echo", ServerName = "demo" }
        });

        var module = new McpModule();

        await using var sp = BuildProvider(manager.Object, registry.Object);

        await module.ActivateAsync(sp, TestContext.Current.CancellationToken);

        manager.Verify(
            m => m.InitializeAsync(It.IsAny<IReadOnlyDictionary<string, McpServerConfig>>(), It.IsAny<CancellationToken>()),
            Times.Once);
        registry.Verify(r => r.Register(It.IsAny<IDynamicToolContributor>()), Times.Once);

        captured.Should().NotBeNull();
        captured!.ModuleId.Should().Be("mcp");
        captured.GetDynamicToolIds().Should().Contain("mcp__demo__echo");
    }

    [Fact]
    public async Task Deactivate_ShutsDownManager_UnregistersToolsAndContributor()
    {
        var registry = new Mock<IDynamicToolContributorRegistry>();
        var manager = new Mock<IMcpManager>();
        manager.Setup(m => m.GetTools()).Returns(Array.Empty<McpToolInfo>());
        var module = new McpModule();

        await using var sp = BuildProvider(manager.Object, registry.Object);

        await module.DeactivateAsync(sp, TestContext.Current.CancellationToken);

        manager.Verify(m => m.ShutdownAsync(It.IsAny<CancellationToken>()), Times.Once);
        manager.Verify(m => m.UnregisterAllToolsAsync(It.IsAny<CancellationToken>()), Times.Once);
        registry.Verify(r => r.Unregister("mcp"), Times.Once);
    }

    [Fact]
    public async Task ActivateDeactivateReactivate_ShouldBeIdempotent()
    {
        var registry = new Mock<IDynamicToolContributorRegistry>();
        var manager = new Mock<IMcpManager>();
        manager.Setup(m => m.GetTools()).Returns(Array.Empty<McpToolInfo>());
        var module = new McpModule();

        await using var sp = BuildProvider(manager.Object, registry.Object);

        await module.ActivateAsync(sp, TestContext.Current.CancellationToken);
        await module.DeactivateAsync(sp, TestContext.Current.CancellationToken);
        await module.ActivateAsync(sp, TestContext.Current.CancellationToken);

        manager.Verify(
            m => m.InitializeAsync(It.IsAny<IReadOnlyDictionary<string, McpServerConfig>>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        manager.Verify(m => m.ShutdownAsync(It.IsAny<CancellationToken>()), Times.Once);
        registry.Verify(r => r.Register(It.IsAny<IDynamicToolContributor>()), Times.Exactly(2));
        registry.Verify(r => r.Unregister("mcp"), Times.Once);
    }

    /// <summary>
    /// UI 声明必须是一条页面贡献：归属自身的 <c>Requires</c>（否则会被注册表以
    /// 「模块贡献 Requires 为空」拒收），且不带 Nav 之外的额外载荷。
    /// </summary>
    [Fact]
    public void DescribeUi_ShouldReturnSinglePageContributionWithOwnership()
    {
        var module = new McpModule();

        var contributions = module.DescribeUi();

        var page = contributions.Should().ContainSingle().Subject.Should().BeOfType<PageContribution>().Subject;
        page.Point.Should().Be("shell.page");
        page.Route.Should().Be("/mcp");
        page.Title.Should().Be("MCP");
        page.ViewKey.Should().Be("page.mcp");
        page.Requires.Should().Equal("mcp",
            "模块贡献必须带自身归属，否则模块停用后页面仍可见而工具已卸载");
        page.Nav.Should().NotBeNull("MCP 页是侧栏入口");
    }

    private static ServiceProvider BuildProvider(IMcpManager manager, IDynamicToolContributorRegistry registry)
    {
        var services = new ServiceCollection();
        services.AddSingleton(manager);
        services.AddSingleton(registry);
        return services.BuildServiceProvider();
    }
}
