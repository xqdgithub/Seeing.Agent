using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Seeing.Agent.Abstractions.Channels;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Comms.Delivery;
using Seeing.Agent.Comms.Extensions;
using Seeing.Agent.Tools.ChannelMessaging;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Comms.Tests.Extensions;

/// <summary>
/// 工具模块的 Noop 解析器与 Comms 真实解析器的注册顺序：
/// 后注册的 Comms 必须覆盖先注册的 Noop；Comms 先注册时 Noop 不得反向覆盖。
/// 用最小 DI 组合，不拉起完整宿主。
/// </summary>
public class CommsRegistrationOrderTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddSeeingComms_ShouldResolveRealResolver_RegardlessOfModuleOrder(bool toolsFirst)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Mock.Of<ISessionManager>());
        services.AddSingleton(Mock.Of<ISessionGroupManager>());
        var registry = Mock.Of<IConfigSectionRegistry>();

        if (toolsFirst)
            new ChannelMessagingModule().ConfigureServices(services);

        services.AddSeeingComms(registry);

        if (!toolsFirst)
            new ChannelMessagingModule().ConfigureServices(services);

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IChannelTargetResolver>()
            .Should().BeOfType<SessionChannelTargetResolver>();
    }
}
