using FluentAssertions;
using Seeing.Agent.Gateway.Permission;
using Xunit;

namespace Seeing.Gateway.Tests.Permission;

/// <summary>
/// GatewaySubscriptionPresenter：固定单会话快照；SurfacedChanged 永不触发。
/// </summary>
public class GatewaySubscriptionPresenterTests
{
    [Fact]
    public void SurfaceSessionIds_ShouldReturnConstructorSessionSnapshot()
    {
        var presenter = new GatewaySubscriptionPresenter("ses_1");

        presenter.SurfaceSessionIds.Should().BeEquivalentTo(new[] { "ses_1" });
    }

    [Fact]
    public void SurfaceSessionIds_ShouldBeStableAcrossReads()
    {
        var presenter = new GatewaySubscriptionPresenter("ses_1");

        presenter.SurfaceSessionIds.Should().BeSameAs(presenter.SurfaceSessionIds);
    }

    [Fact]
    public void SurfacedChanged_ShouldNeverBeRaised()
    {
        var presenter = new GatewaySubscriptionPresenter("ses_1");
        Action handler = () => throw new Xunit.Sdk.XunitException("SurfacedChanged 不应触发");
        presenter.SurfacedChanged += handler;

        // 无任何内部状态变化入口；附加/移除订阅本身不应触发事件
        presenter.SurfacedChanged -= handler;
    }
}
