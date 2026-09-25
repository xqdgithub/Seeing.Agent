using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Seeing.Agent.Abstractions.Skills;
using Seeing.Agent.Core.Tools.SystemOne;
using Xunit;

namespace Seeing.Agent.Tools.SystemOne.Tests;

/// <summary>SystemOne 内嵌 Skill 随模块 Activate/Deactivate 注册与注销。</summary>
public class SystemOneSkillRegistrationTests
{
    private static ServiceProvider BuildProvider(ISkillManager skillManager)
    {
        var services = new ServiceCollection();
        services.AddSingleton(skillManager);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Activate_应注册内嵌技能_Deactivate_应注销()
    {
        SkillInfo? captured = null;
        var skillManager = new Mock<ISkillManager>();
        skillManager
            .Setup(m => m.RegisterEmbeddedSkill(It.IsAny<SkillInfo>()))
            .Callback<SkillInfo>(s => captured = s);

        var module = new SystemOneToolsModule();
        await using var provider = BuildProvider(skillManager.Object);

        await module.ActivateAsync(provider);

        captured.Should().NotBeNull();
        captured!.Name.Should().Be("systemone-judgment");
        captured.Description.Should().NotBeNullOrEmpty();
        captured.Content.Should().Contain("systemone_ask");
        captured.Content.Should().Contain("systemone_noul");
        captured.Location.Should().StartWith("systemone/");

        await module.DeactivateAsync(provider);

        skillManager.Verify(m => m.Unregister("systemone-judgment"), Times.Once);
    }

    [Fact]
    public async Task Activate_无技能管理器_不应抛()
    {
        var services = new ServiceCollection();
        await using var provider = services.BuildServiceProvider();
        var module = new SystemOneToolsModule();

        var act = async () => await module.ActivateAsync(provider);

        await act.Should().NotThrowAsync();
    }
}
