using FluentAssertions;
using Seeing.Agent.Cli.Services;
using Xunit;

namespace Seeing.Agent.Cli.Tests;

public class ServiceRunModeResolverTests
{
    [Fact]
    public void DefaultFor_WebUi_ShouldBeForeground()
    {
        ServiceRunModeResolver.DefaultFor("webui").Should().Be(ServiceRunMode.Foreground);
    }

    [Fact]
    public void DefaultFor_Gateway_ShouldBeBackground()
    {
        ServiceRunModeResolver.DefaultFor("gateway").Should().Be(ServiceRunMode.Background);
    }

    [Fact]
    public void TryResolve_BackgroundSwitch_ShouldOverrideWebUiDefault()
    {
        var ok = ServiceRunModeResolver.TryResolve(
            "webui", background: true, foreground: false, out var mode, out var error);

        ok.Should().BeTrue();
        mode.Should().Be(ServiceRunMode.Background);
        error.Should().BeNull();
    }

    [Fact]
    public void TryResolve_ForegroundSwitch_ShouldOverrideGatewayDefault()
    {
        var ok = ServiceRunModeResolver.TryResolve(
            "gateway", background: false, foreground: true, out var mode, out var error);

        ok.Should().BeTrue();
        mode.Should().Be(ServiceRunMode.Foreground);
        error.Should().BeNull();
    }

    [Fact]
    public void TryResolve_BothSwitches_ShouldFail()
    {
        var ok = ServiceRunModeResolver.TryResolve(
            "webui", background: true, foreground: true, out _, out var error);

        ok.Should().BeFalse();
        error.Should().Contain("不能同时");
    }
}
