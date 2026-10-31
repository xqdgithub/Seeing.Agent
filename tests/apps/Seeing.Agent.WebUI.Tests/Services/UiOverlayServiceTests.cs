using FluentAssertions;
using Seeing.Agent.WebUI.Services;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

/// <summary>跨组件 UI 覆盖层请求（主页卡片 → 审批中心抽屉）。</summary>
public class UiOverlayServiceTests
{
    [Fact]
    public void RequestPermissionCenter_ShouldRaiseEvent()
    {
        var service = new UiOverlayService();
        var raised = 0;
        service.PermissionCenterRequested += () => raised++;

        service.RequestPermissionCenter();

        raised.Should().Be(1);
    }

    [Fact]
    public void RequestPermissionCenter_WithoutSubscriber_ShouldNotThrow()
    {
        var service = new UiOverlayService();

        var act = () => service.RequestPermissionCenter();

        act.Should().NotThrow();
    }

    [Fact]
    public void Unsubscribe_ShouldStopReceiving()
    {
        var service = new UiOverlayService();
        var raised = 0;
        void Handler() => raised++;

        service.PermissionCenterRequested += Handler;
        service.PermissionCenterRequested -= Handler;
        service.RequestPermissionCenter();

        raised.Should().Be(0);
    }
}
