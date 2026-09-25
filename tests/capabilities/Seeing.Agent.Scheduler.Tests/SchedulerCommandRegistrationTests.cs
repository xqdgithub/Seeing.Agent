using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Core.Commands;
using Seeing.Agent.Scheduler.Abstractions;
using Seeing.Agent.Scheduler.Hosting;
using Xunit;

namespace Seeing.Agent.Scheduler.Tests;

/// <summary>Scheduler 命令随模块 Activate/Deactivate 注册与注销。</summary>
public class SchedulerCommandRegistrationTests
{
    private static readonly string[] ExpectedCommands = ["cron-list", "cron-run", "heartbeat-run"];

    [Fact]
    public async Task Activate_注册命令_Deactivate_注销()
    {
        var registry = new CommandRegistry();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICommandRegistry>(registry);
        await using var provider = services.BuildServiceProvider();

        var module = new SchedulerModule(new SchedulerModuleActivity(), Mock.Of<IScheduleManager>());

        await module.ActivateAsync(provider);

        foreach (var name in ExpectedCommands)
            registry.HasCommand(name).Should().BeTrue($"command '{name}' should be registered");

        await module.DeactivateAsync(provider);

        foreach (var name in ExpectedCommands)
            registry.HasCommand(name).Should().BeFalse($"command '{name}' should be unregistered");
    }
}
