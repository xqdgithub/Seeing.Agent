using FluentAssertions;
using Moq;
using PuppeteerSharp;
using Seeing.Agent.Tools.Browser;
using Xunit;

namespace Seeing.Agent.Tools.Browser.Tests;

/// <summary>
/// 建页策略守门：每个页面必须是一个独立<b>窗口</b>，不能是标签页。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么这条必须钉住</b>：实测 <c>browser.NewPageAsync()</c> 建的是标签页，
/// 浏览器只会给「当前可见的那一个」标签页出 screencast 帧 —— 后台标签页掉到 <b>0 帧</b>。
/// 多实例共用同一个浏览器时，这意味着「只有一个工作台有画面，其它全黑」。
/// 改用 <c>CreatePageOptions { Type = CreatePageType.Window }</c>（每页一个窗口）后两路各约 60fps。
/// </para>
/// <para>
/// 这里只用假对象钉「建页时传了什么参数」；真实帧率由
/// <c>BrowserMultiWindowFrameRateTests</c>（RequiresBrowser）验收。
/// </para>
/// </remarks>
public class BrowserPageFactoryTests
{
    [Fact]
    public void Options_ShouldCreateWindow()
    {
        BrowserPageFactory.Options().Type.Should().Be(CreatePageType.Window);
    }

    [Fact]
    public void Options_ShouldReturnNewInstanceEachCall()
    {
        // 共享同一个实例会让调用方的修改（例如将来补 WindowBounds）串场到其它页面
        BrowserPageFactory.Options().Should().NotBeSameAs(BrowserPageFactory.Options());
    }

    [Fact]
    public async Task CreateAsync_WithoutContext_ShouldCreateWindowOnBrowser()
    {
        var page = new Mock<IPage>().Object;
        var browser = new Mock<IBrowser>();
        browser
            .Setup(b => b.NewPageAsync(It.Is<CreatePageOptions>(o => o.Type == CreatePageType.Window)))
            .ReturnsAsync(page);

        var created = await BrowserPageFactory.CreateAsync(browser.Object, context: null, CancellationToken.None);

        created.Should().BeSameAs(page);
        browser.Verify(b => b.NewPageAsync(It.Is<CreatePageOptions>(o => o.Type == CreatePageType.Window)), Times.Once);
    }

    [Fact]
    public async Task CreateAsync_WithContext_ShouldCreateWindowOnContext()
    {
        var page = new Mock<IPage>().Object;
        var browser = new Mock<IBrowser>();
        var context = new Mock<IBrowserContext>();
        context
            .Setup(c => c.NewPageAsync(It.Is<CreatePageOptions>(o => o.Type == CreatePageType.Window)))
            .ReturnsAsync(page);

        var created = await BrowserPageFactory.CreateAsync(browser.Object, context.Object, CancellationToken.None);

        created.Should().BeSameAs(page);
        context.Verify(c => c.NewPageAsync(It.Is<CreatePageOptions>(o => o.Type == CreatePageType.Window)), Times.Once);
        browser.Verify(b => b.NewPageAsync(It.IsAny<CreatePageOptions>()), Times.Never);
    }
}
