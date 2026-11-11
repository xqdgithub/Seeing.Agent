using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Interactions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Abstractions.Ui;
using Seeing.Agent.Abstractions.Ui.Workbench;
using Seeing.Agent.Tools.Browser;
using Seeing.Agent.Tools.Browser.Tools;
using Xunit;

namespace Seeing.Agent.Tools.Browser.Tests;

/// <summary>
/// <c>browser_open</c> 必须同时「建页面」与「打开工作台面板」。
/// </summary>
/// <remarks>
/// <para>
/// <b>用户报障</b>：「调用 browser_open 并不会打开工作台」。
/// 症状是 agent 工具执行成功、后续工具也能操作，但界面上什么都没有 —— 用户不知道发生了什么。
/// </para>
/// <para>
/// 这里钉住两个契约：
/// ① 有界面时发出 <c>workbench.registry.open</c> 请求且 <c>workbenchId = browser</c>；
/// ② 无界面 / 通道缺失时**不报错**，页面照建（headless、Gateway、CLI 都必须能用）。
/// </para>
/// </remarks>
public class BrowserOpenToolWorkbenchTests
{
    /// <summary>工作台 id 与模块 id 必须同源。</summary>
    [Fact]
    public void WorkbenchId_ShouldMatchModuleIdAndViewKey()
    {
        BrowserModule.IdValue.Should().Be(new BrowserModule().Id);
        WorkbenchPoints.ViewKeyFor(BrowserModule.IdValue).Should().Be("workbench.browser");
    }

    /// <summary>扩展点缺失（无界面宿主）时不得发请求。</summary>
    [Fact]
    public async Task Open_WithoutPointCatalog_ShouldNotRequestWorkbench()
    {
        var manager = new Mock<IHostRequestManager>();
        var tool = NewTool(new Mock<IBrowserSessionHost>().Object);

        // 页面创建会失败（host mock 无 GetPageAsync），所以先断言「不发工作台请求」
        await tool.ExecuteAsync(
            JsonDocument.Parse("""{"url":"about:blank"}""").RootElement,
            Context(manager, points: null));

        manager.Verify(
            m => m.BeginAsync(It.IsAny<HostRequest>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "无界面宿主不该发工作台请求");
    }

    /// <summary>宿主请求通道缺失时不得抛异常（headless / 单测环境）。</summary>
    [Fact]
    public async Task Open_WithoutHostRequestManager_ShouldNotThrow()
    {
        var tool = NewTool(new Mock<IBrowserSessionHost>().Object);

        var act = async () => await tool.ExecuteAsync(
            JsonDocument.Parse("""{"url":"about:blank"}""").RootElement,
            Context(manager: null, points: FakePoints()));

        var result = await act.Should().NotThrowAsync();
        result.Subject.Success.Should().BeFalse("页面建不出来时必须是可读失败");
        result.Subject.Error.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>工具描述必须说明它会打开界面面板，否则模型不会用它来「让用户看到」。</summary>
    [Fact]
    public void Description_ShouldMentionWorkbench()
    {
        var tool = NewTool(new Mock<IBrowserSessionHost>().Object);

        tool.Description.Should().Contain("工作台",
            "不写明会开面板，模型只把它当「后台建页面」，用户仍然看不到任何东西");
    }

    private static BrowserOpenTool NewTool(IBrowserSessionHost host)
        => new(
            NullLogger<BrowserOpenTool>.Instance,
            host,
            new TestOptionsMonitor<BrowserOptions>(new BrowserOptions()));

    private static ToolContext Context(Mock<IHostRequestManager>? manager, IUiPointCatalog? points)
    {
        var services = new Mock<IServiceProvider>();
        services.Setup(s => s.GetService(typeof(IHostRequestManager))).Returns(manager?.Object!);
        services.Setup(s => s.GetService(typeof(IUiPointCatalog))).Returns(points!);

        return new ToolContext
        {
            SessionId = "s1",
            CallId = "call1",
            Services = services.Object,
            CancellationToken = TestContext.Current.CancellationToken
        };
    }

    /// <summary>声明了工作台扩展点的假目录。</summary>
    private static IUiPointCatalog FakePoints()
    {
        var points = new Mock<IUiPointCatalog>();
        points.Setup(p => p.Find(WorkbenchPoints.DockId))
            .Returns(new UiPoint
            {
                Id = WorkbenchPoints.DockId,
                DisplayName = "浏览器",
                PayloadKind = UiPayloadKind.Workbench,
                Scope = UiVisibilityScope.Session
            });
        return points.Object;
    }
}
