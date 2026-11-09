using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Gateway.Configuration;
using Seeing.Agent.Gateway.Hosting;
using Xunit;

namespace Seeing.Gateway.Tests.Hosting;

/// <summary>
/// 「Gateway 此刻应当自动运行吗」的三条约束：模块结算 → Enabled → AutoStart。
/// 宿主启动与工作区切换共用这一处判定（分散写过一次就出过「切换后不启动」）。
/// </summary>
public class GatewayAutoStartPolicyTests
{
    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(true, true, false, false)]   // AutoStart 关：不自动启动
    [InlineData(true, false, true, false)]   // Enabled 关：不自动启动、也不允许运行
    [InlineData(false, true, true, false)]   // 模块被能力集禁用：端口不得监听
    public void IsAutoStartRequested_ShouldRequireAllThree(
        bool moduleEnabled, bool enabled, bool autoStart, bool expected)
    {
        var policy = Create(moduleEnabled, enabled, autoStart);

        policy.IsAutoStartRequested.Should().Be(expected);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]  // 模块禁用优先于 Enabled
    public void IsEnabled_ShouldRequireModuleAndEnabled_ButNotAutoStart(
        bool moduleEnabled, bool enabled, bool expected)
    {
        var policy = Create(moduleEnabled, enabled, autoStart: false);

        policy.IsEnabled.Should().Be(expected);
    }

    [Fact]
    public void WithoutModuleCatalog_ShouldTreatModuleAsEnabled()
    {
        // 未接入结算的宿主（少数测试/嵌入场景）不应因为拿不到模块目录就永久不启动。
        var policy = new GatewayAutoStartPolicy(
            Options.Create(new GatewayOptions { Enabled = true, AutoStart = true }));

        policy.IsModuleEnabled.Should().BeTrue();
        policy.IsAutoStartRequested.Should().BeTrue();
    }

    private static GatewayAutoStartPolicy Create(bool moduleEnabled, bool enabled, bool autoStart)
    {
        var catalog = new Mock<IModuleCatalog>();
        catalog.Setup(c => c.IsEnabled("gateway")).Returns(moduleEnabled);

        return new GatewayAutoStartPolicy(
            Options.Create(new GatewayOptions { Enabled = enabled, AutoStart = autoStart }),
            catalog.Object);
    }
}
