using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Chat;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Hosting.Tui;
using Seeing.Agent.Hosting.Tui.Permissions;

namespace Seeing.Agent.Tui.Tests.Services;

/// <summary>
/// 组合根接线冒烟：<c>AddSeeingHostingTui</c> 的最小可解析契约（不启动真实 Host）。
/// 完整 TuiChatEngine 解析依赖整条核心图，此处只做注册断言 + 无依赖服务解析。
/// </summary>
public sealed class TuiChatEngineWiringTests
{
    [Fact]
    public void AddSeeingHostingTui_ShouldRegisterHostShapeAndCorePorts()
    {
        var services = new ServiceCollection();

        services.AddSeeingHostingTui();

        services.Should().Contain(d => d.ServiceType == typeof(HostShapeDescriptor));
        services.Should().Contain(d => d.ServiceType == typeof(IChatOrchestrator));
        services.Should().Contain(d => d.ServiceType == typeof(IExecutionSubmitter));
        services.Should().Contain(d => d.ServiceType == typeof(IPermissionChannel));

        using var provider = services.BuildServiceProvider();

        var descriptor = provider.GetRequiredService<HostShapeDescriptor>();
        descriptor.Id.Should().Be("tui");
        descriptor.DefaultScenario.Should().Be("full");

        provider.GetRequiredService<IPermissionChannel>().Should().BeOfType<TuiPermissionChannel>();
    }
}
