using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Seeing.Agent.Abstractions.Skills;
using Xunit;

namespace Seeing.Agent.Tools.SystemOne.Tests;

/// <summary>SystemOne 内嵌 Skill 随模块 Activate/Deactivate 注册与注销（写入口为 ISkillCatalog）。</summary>
public class SystemOneSkillRegistrationTests
{
    private static ServiceProvider BuildProvider(ISkillCatalog catalog)
    {
        var services = new ServiceCollection();
        services.AddSingleton(catalog);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Activate_应注册内嵌技能_Deactivate_应注销()
    {
        SkillInfo? captured = null;
        var providerId = string.Empty;

        var catalog = new Mock<ISkillCatalog>();
        catalog
            .Setup(c => c.RegisterAsync(It.IsAny<SkillInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<SkillInfo, string, CancellationToken>((skill, provider, _) =>
            {
                captured = skill;
                providerId = provider;
            })
            .ReturnsAsync(true);

        var module = new SystemOneToolsModule();
        await using var provider = BuildProvider(catalog.Object);

        await module.ActivateAsync(provider);

        captured.Should().NotBeNull();
        captured!.Name.Should().Be("systemone-judgment");
        captured.Description.Should().NotBeNullOrEmpty();
        captured.Content.Should().Contain("systemone_ask");
        captured.Content.Should().Contain("systemone_noul");
        captured.Location.Should().StartWith("systemone/");
        providerId.Should().Be("systemone.tools");

        await module.DeactivateAsync(provider);

        catalog.Verify(
            c => c.UnregisterAsync("systemone-judgment", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Activate_无技能目录_不应抛()
    {
        var services = new ServiceCollection();
        await using var provider = services.BuildServiceProvider();
        var module = new SystemOneToolsModule();

        var act = async () => await module.ActivateAsync(provider);

        await act.Should().NotThrowAsync();
    }
}
