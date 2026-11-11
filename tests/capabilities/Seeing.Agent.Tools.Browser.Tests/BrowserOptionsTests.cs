using FluentAssertions;
using Seeing.Agent.Tools.Browser;
using Xunit;

namespace Seeing.Agent.Tools.Browser.Tests;

/// <summary>
/// 配置钳制：配置写坏（0 或极大值）不得让页面变成 1px 或让超时变成 0。
/// <para>这类缺陷在真实使用中表现为「浏览器打开了但什么都看不见」，极难排查。</para>
/// </summary>
public class BrowserOptionsTests
{
    [Fact]
    public void Defaults_ShouldBeSane()
    {
        var options = new BrowserOptions();

        options.Headless.Should().BeTrue("默认必须能跑在无桌面环境的宿主里");
        options.ExecutablePath.Should().BeNull();
        options.IdleSeconds.Should().Be(600);
        options.EffectiveWidth.Should().Be(1280);
        options.EffectiveHeight.Should().Be(800);
        options.AllowPrivateHosts.Should().BeFalse("默认拒绝内网，与 webfetch 同源策略");
        options.OperationTimeout.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Theory]
    [InlineData(0, 320)]
    [InlineData(-100, 320)]
    [InlineData(1, 320)]
    [InlineData(1920, 1920)]
    [InlineData(99999, 4096)]
    public void EffectiveWidth_ShouldClamp(int configured, int expected)
    {
        new BrowserOptions { ViewportWidth = configured }.EffectiveWidth.Should().Be(expected);
    }

    [Theory]
    [InlineData(0, 240)]
    [InlineData(1080, 1080)]
    [InlineData(99999, 4096)]
    public void EffectiveHeight_ShouldClamp(int configured, int expected)
    {
        new BrowserOptions { ViewportHeight = configured }.EffectiveHeight.Should().Be(expected);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(9999, 300)]
    [InlineData(45, 45)]
    public void OperationTimeout_ShouldClamp(int seconds, int expected)
    {
        new BrowserOptions { DefaultTimeoutSeconds = seconds }
            .OperationTimeout.Should().Be(TimeSpan.FromSeconds(expected));
    }

    [Fact]
    public void IdleTimeout_ZeroOrNegative_ShouldDisableReaping()
    {
        new BrowserOptions { IdleSeconds = 0 }.IdleTimeout.Should().BeNull();
        new BrowserOptions { IdleSeconds = -1 }.IdleTimeout.Should().BeNull();
        new BrowserOptions { IdleSeconds = 60 }.IdleTimeout.Should().Be(TimeSpan.FromSeconds(60));
    }
}
