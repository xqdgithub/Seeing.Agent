using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Seeing.Agent.Abstractions.Background;
using Seeing.Agent.WebUI.Services;

namespace Seeing.Agent.WebUI.Tests.Services;

/// <summary>
/// <see cref="BackgroundActivityUiService.DetachAsync"/> 的端口契约：
/// 控制面是可选的，任何缺失或抛错都必须折叠成 <c>false</c>，不能把异常带进 UI。
/// </summary>
public class BackgroundActivityUiServiceDetachTests
{
    [Fact]
    public async Task DetachAsync_ShouldForwardToControl()
    {
        var control = new Mock<IBackgroundActivityControl>();
        control
            .Setup(c => c.DetachAsync("act1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var service = CreateService(control.Object);

        var ok = await service.DetachAsync("act1", TestContext.Current.CancellationToken);

        ok.Should().BeTrue();
        control.Verify(c => c.DetachAsync("act1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DetachAsync_WhenControlRejects_ShouldReturnFalse()
    {
        var control = new Mock<IBackgroundActivityControl>();
        control
            .Setup(c => c.DetachAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var service = CreateService(control.Object);

        (await service.DetachAsync("act1", TestContext.Current.CancellationToken)).Should().BeFalse("没有前台等待者时只是没放行，不是错误");
    }

    [Fact]
    public async Task DetachAsync_WithoutControl_ShouldReturnFalse()
    {
        var service = new BackgroundActivityUiService(
            new ServiceCollection().BuildServiceProvider());

        (await service.DetachAsync("act1", TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    [Fact]
    public async Task DetachAsync_WhenControlThrows_ShouldReturnFalse()
    {
        var control = new Mock<IBackgroundActivityControl>();
        control
            .Setup(c => c.DetachAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var service = CreateService(control.Object);

        (await service.DetachAsync("act1", TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    [Fact]
    public async Task DetachAsync_WithEmptyId_ShouldNotTouchControl()
    {
        var control = new Mock<IBackgroundActivityControl>();
        var service = CreateService(control.Object);

        (await service.DetachAsync(string.Empty, TestContext.Current.CancellationToken)).Should().BeFalse();

        control.Verify(
            c => c.DetachAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static BackgroundActivityUiService CreateService(IBackgroundActivityControl control)
    {
        var services = new ServiceCollection();
        services.AddSingleton(control);
        return new BackgroundActivityUiService(services.BuildServiceProvider());
    }
}
