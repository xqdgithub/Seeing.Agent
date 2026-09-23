using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Skills;
using Seeing.Agent.Core.Tools.SystemOne.Skills;
using Xunit;

namespace Seeing.Agent.Tools.SystemOne.Tests;

public class SystemOneSkillRegistrationTests
{
    private static ServiceProvider BuildProvider(ISkillManager? skillManager)
    {
        var services = new ServiceCollection();
        if (skillManager is not null)
            services.AddSingleton(skillManager);

        services.AddSingleton<ILogger<SystemOneSkillRegistrationHostedService>>(
            NullLogger<SystemOneSkillRegistrationHostedService>.Instance);
        services.AddSingleton<SystemOneSkillRegistrationHostedService>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task StartAsync_有技能管理器_应注册内嵌技能()
    {
        SkillInfo? captured = null;
        var skillManager = new Mock<ISkillManager>();
        skillManager
            .Setup(m => m.RegisterEmbeddedSkill(It.IsAny<SkillInfo>()))
            .Callback<SkillInfo>(s => captured = s);

        await using var provider = BuildProvider(skillManager.Object);
        var hosted = provider.GetRequiredService<SystemOneSkillRegistrationHostedService>();

        await hosted.StartAsync();

        captured.Should().NotBeNull();
        captured!.Name.Should().Be("systemone-judgment");
        captured.Description.Should().NotBeNullOrEmpty();
        captured.Content.Should().Contain("systemone_ask");
        captured.Location.Should().StartWith("systemone/");
        hosted.IsRunning.Should().BeTrue();
    }

    [Fact]
    public async Task StartAsync_无技能管理器_不应抛()
    {
        await using var provider = BuildProvider(skillManager: null);
        var hosted = provider.GetRequiredService<SystemOneSkillRegistrationHostedService>();

        var act = async () => await hosted.StartAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void ModuleId_应为systemoneTools()
    {
        using var provider = BuildProvider(skillManager: null);
        var hosted = provider.GetRequiredService<SystemOneSkillRegistrationHostedService>();

        hosted.ModuleId.Should().Be("systemone.tools");
    }
}
