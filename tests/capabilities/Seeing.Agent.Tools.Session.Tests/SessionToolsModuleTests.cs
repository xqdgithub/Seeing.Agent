using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Core.Tools.Session.Tests;

public class SessionToolsModuleTests
{
    [Fact]
    public void Module_Id_IsSessionTools()
    {
        var module = new SessionToolsModule();
        module.Id.Should().Be("session.tools");
    }

    [Fact]
    public void ProvidedTools_ContainsFiveSessionTools()
    {
        var module = new SessionToolsModule();
        module.ProvidedTools.Should().BeEquivalentTo(
        [
            "session_list",
            "session_search",
            "session_read",
            "session_trim",
            "session_handoff",
        ]);
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
    public async Task Activate_Should_RegisterAllFiveTools_WhenSubmitterAvailable()
    {
        var (manager, registered, _) = BuildToolManager();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Mock.Of<ISessionManager>());
        services.AddSingleton(Mock.Of<ISessionGroupManager>());
        services.AddSingleton(Mock.Of<IExecutionSubmitter>());
        services.AddSingleton(manager.Object);

        var module = new SessionToolsModule();
        module.ConfigureServices(services);
        using var provider = services.BuildServiceProvider();

        await module.ActivateAsync(provider, TestContext.Current.CancellationToken);

        registered.Should().BeEquivalentTo(
        [
            "session_list",
            "session_search",
            "session_read",
            "session_trim",
            "session_handoff",
        ]);
    }

    [Fact]
    public async Task Activate_Should_SkipHandoff_WhenSubmitterMissing()
    {
        var (manager, registered, _) = BuildToolManager();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Mock.Of<ISessionManager>());
        services.AddSingleton(Mock.Of<ISessionGroupManager>());
        services.AddSingleton(manager.Object);

        var module = new SessionToolsModule();
        module.ConfigureServices(services);
        using var provider = services.BuildServiceProvider();

        await module.ActivateAsync(provider, TestContext.Current.CancellationToken);

        registered.Should().BeEquivalentTo(
        [
            "session_list",
            "session_search",
            "session_read",
            "session_trim",
        ]);
        registered.Should().NotContain("session_handoff");
    }

    [Fact]
    public async Task Deactivate_Should_UnregisterAllProvidedTools()
    {
        var (manager, _, unregistered) = BuildToolManager();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Mock.Of<ISessionManager>());
        services.AddSingleton(Mock.Of<ISessionGroupManager>());
        services.AddSingleton(Mock.Of<IExecutionSubmitter>());
        services.AddSingleton(manager.Object);

        var module = new SessionToolsModule();
        module.ConfigureServices(services);
        using var provider = services.BuildServiceProvider();

        await module.ActivateAsync(provider, TestContext.Current.CancellationToken);
        await module.DeactivateAsync(provider, TestContext.Current.CancellationToken);

        unregistered.Should().BeEquivalentTo(module.ProvidedTools);
    }
}
