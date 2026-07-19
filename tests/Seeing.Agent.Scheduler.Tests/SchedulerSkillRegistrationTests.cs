using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Scheduler.Skills;
using Seeing.Agent.Skills;
using Xunit;

namespace Seeing.Agent.Scheduler.Tests;

public class SchedulerSkillRegistrationTests
{
    private static readonly string[] ExpectedSkillNames =
    [
        "cron-management",
        "cron-create",
        "cron-list-run",
        "cron-lifecycle"
    ];

    [Fact]
    public async Task StartAsync_WithSkillManager_ShouldRegisterFourEmbeddedSkills()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<SkillManager>();
        services.AddHostedService<SchedulerSkillRegistrationHostedService>();

        await using var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>()
            .OfType<SchedulerSkillRegistrationHostedService>()
            .Single();

        await hosted.StartAsync(CancellationToken.None);

        var skillManager = provider.GetRequiredService<SkillManager>();
        foreach (var name in ExpectedSkillNames)
        {
            var skill = skillManager.GetSkillInfo(name);
            skill.Should().NotBeNull($"skill '{name}' should be registered");
            skill!.Location.Should().Be($"embedded://scheduler/{name}");
            skill.Description.Should().NotBeNullOrWhiteSpace();
            skill.Content.Should().NotBeNullOrWhiteSpace();
        }

        ExpectedSkillNames.Count(n => skillManager.GetSkillInfo(n) is not null).Should().Be(4);
    }

    [Fact]
    public async Task StartAsync_WithoutSkillManager_ShouldSkipWithoutThrowing()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHostedService<SchedulerSkillRegistrationHostedService>();

        await using var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>()
            .OfType<SchedulerSkillRegistrationHostedService>()
            .Single();

        var act = async () => await hosted.StartAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();
    }
}
