using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Seeing.Agent.Abstractions.Skills;
using Seeing.Agent.Scheduler.Abstractions;
using Seeing.Agent.Scheduler.Hosting;
using Seeing.Agent.Skills;
using Seeing.Agent.Skills.Catalog;
using Xunit;

namespace Seeing.Agent.Scheduler.Tests;

/// <summary>Scheduler 内嵌 Skill / 命令随模块 Activate/Deactivate 注册与注销。</summary>
public class SchedulerSkillRegistrationTests
{
    private static readonly string[] ExpectedSkillNames =
    [
        "cron-management",
        "cron-create",
        "cron-list-run",
        "cron-lifecycle"
    ];

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(sp => new SkillCatalogCore(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SkillCatalogCore>.Instance));
        services.AddSingleton<ISkillCatalog>(sp => sp.GetRequiredService<SkillCatalogCore>());
        services.AddSingleton<SkillManager>();
        services.AddSingleton<ISkillManager>(sp => sp.GetRequiredService<SkillManager>());
        return services.BuildServiceProvider();
    }

    private static SchedulerModule CreateModule() =>
        new(new SchedulerModuleActivity(), Mock.Of<IScheduleManager>());

    [Fact]
    public async Task Activate_注册四个内嵌技能_Deactivate_全部注销()
    {
        await using var provider = BuildProvider();
        var module = CreateModule();

        await module.ActivateAsync(provider);

        var catalog = provider.GetRequiredService<ISkillCatalog>();
        foreach (var name in ExpectedSkillNames)
        {
            var entry = catalog.Find(name);
            entry.Should().NotBeNull($"skill '{name}' should be registered");
            entry!.Info.Location.Should().Be($"scheduler/{name}");
            entry.Info.Description.Should().NotBeNullOrWhiteSpace();
            entry.Info.Content.Should().NotBeNullOrWhiteSpace();
            entry.ProviderId.Should().Be("scheduler");
        }
        ExpectedSkillNames.Count(n => catalog.Find(n) is not null).Should().Be(4);

        await module.DeactivateAsync(provider);

        foreach (var name in ExpectedSkillNames)
            catalog.Find(name).Should().BeNull($"skill '{name}' should be unregistered");
    }

    [Fact]
    public async Task Activate_无技能管理器_不应抛()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();
        var module = CreateModule();

        var act = async () => await module.ActivateAsync(provider);

        await act.Should().NotThrowAsync();
    }
}
