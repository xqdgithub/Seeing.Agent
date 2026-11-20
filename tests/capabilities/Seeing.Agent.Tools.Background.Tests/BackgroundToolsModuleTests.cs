using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Background;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Tools;
using Xunit;

namespace Seeing.Agent.Tools.Background.Tests;

public class BackgroundToolsModuleTests
{
    private static readonly string[] s_expectedTools =
    [
        "activity_status",
        "activity_output",
        "activity_wait",
        "activity_cancel",
        "activity_list",
    ];

    [Fact]
    public void ProvidedTools_ShouldBeExactlyFiveWithExpectedNames()
    {
        var moduleType = typeof(BackgroundToolsModule);
        moduleType.Should().Implement<ISeeingModule>();

        var module = (ISeeingModule)Activator.CreateInstance(moduleType)!;

        module.Id.Should().Be("background");
        module.ProvidedTools.Should().HaveCount(5).And.BeEquivalentTo(s_expectedTools);
    }

    [Fact]
    public void EveryConcreteTool_ShouldMatchDeclaredTools()
    {
        var fake = new FakeBackgroundActivityManager();
        var toolTypes = typeof(BackgroundToolsModule).Assembly.GetTypes()
            .Where(t => typeof(ITool).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
            .ToList();

        toolTypes.Should().HaveCount(5);

        var ids = toolTypes
            .Select(t => (ITool)Activator.CreateInstance(t, NullLoggerInstance(t), fake)!)
            .Select(t => t.Id)
            .ToList();

        ids.Should().BeEquivalentTo(s_expectedTools);
    }

    private static object NullLoggerInstance(Type toolType)
    {
        var nullLoggerType = typeof(NullLogger<>).MakeGenericType(toolType);
        return nullLoggerType
                   .GetField("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                   ?.GetValue(null)
               ?? Activator.CreateInstance(nullLoggerType)!;
    }

    [Fact]
    public void Tools_ShouldDeclareExpectedCapabilities()
    {
        var fake = new FakeBackgroundActivityManager();

        var status = new ActivityStatusTool(NullLogger<ActivityStatusTool>.Instance, fake);
        status.Capabilities.Should().ContainKey(ToolCapabilityKeys.LoopSkip);

        var output = new ActivityOutputTool(NullLogger<ActivityOutputTool>.Instance, fake);
        output.Capabilities.Should().ContainKey(ToolCapabilityKeys.OutputSkip);

        var wait = new ActivityWaitTool(NullLogger<ActivityWaitTool>.Instance, fake);
        wait.Capabilities.Should()
            .ContainKey(ToolCapabilityKeys.TimeoutSkip)
            .And.ContainKey(ToolCapabilityKeys.LoopSkip);

        var cancel = new ActivityCancelTool(NullLogger<ActivityCancelTool>.Instance, fake);
        cancel.Capabilities.Should().BeNull();

        var list = new ActivityListTool(NullLogger<ActivityListTool>.Instance, fake);
        list.Capabilities.Should().BeNull();
    }

    private static (Mock<IToolManager> Manager, List<string> Registered, List<string> Unregistered) BuildToolManager()
    {
        var registered = new List<string>();
        var unregistered = new List<string>();

        var manager = new Mock<IToolManager>();
        manager.Setup(t => t.RegisterToolAsync(It.IsAny<ITool>(), It.IsAny<CancellationToken>()))
               .Callback<ITool, CancellationToken>((tool, _) => registered.Add(tool.Id))
               .Returns(Task.CompletedTask);
        manager.Setup(t => t.UnregisterToolAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
               .Callback<string, CancellationToken>((id, _) => unregistered.Add(id))
               .ReturnsAsync(true);

        return (manager, registered, unregistered);
    }

    [Fact]
    public async Task Activate_Should_RegisterAllFiveTools()
    {
        var (manager, registered, _) = BuildToolManager();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Mock.Of<IBackgroundActivityManager>());
        services.AddSingleton(manager.Object);

        var module = new BackgroundToolsModule();
        module.ConfigureServices(services);
        using var provider = services.BuildServiceProvider();

        await module.ActivateAsync(provider, TestContext.Current.CancellationToken);

        registered.Should().BeEquivalentTo(s_expectedTools);
    }

    [Fact]
    public async Task Deactivate_Should_UnregisterAllProvidedTools()
    {
        var (manager, _, unregistered) = BuildToolManager();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Mock.Of<IBackgroundActivityManager>());
        services.AddSingleton(manager.Object);

        var module = new BackgroundToolsModule();
        module.ConfigureServices(services);
        using var provider = services.BuildServiceProvider();

        await module.ActivateAsync(provider, TestContext.Current.CancellationToken);
        await module.DeactivateAsync(provider, TestContext.Current.CancellationToken);

        unregistered.Should().BeEquivalentTo(module.ProvidedTools);
    }
}
