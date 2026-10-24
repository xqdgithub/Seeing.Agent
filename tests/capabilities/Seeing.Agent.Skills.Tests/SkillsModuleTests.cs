using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Prompts;
using Seeing.Agent.Skills.Catalog;
using Xunit;

namespace Seeing.Agent.Skills.Tests;

public class SkillsModuleTests
{
    [Fact]
    public void Module_Id_IsSkills()
    {
        var module = new SkillsModule();
        module.Id.Should().Be("skills");
    }

    [Fact]
    public void ProvidedSeams_ContainsSkills()
    {
        var module = new SkillsModule();
        module.ProvidedSeams.Should().Equal("skills");
    }

    [Fact]
    public void ProvidedTools_ContainsSkill()
    {
        var module = new SkillsModule();
        module.ProvidedTools.Should().Contain("skill");
    }

    [Fact]
    public void DependsOn_IsEmpty()
    {
        var module = new SkillsModule();
        module.DependsOn.Should().BeEmpty();
    }
}

public class SkillManagerPromptSectionTests
{
    private static SkillManager CreateManager() => new(
        NullLogger<SkillManager>.Instance,
        new SkillCatalogCore(NullLogger<SkillCatalogCore>.Instance));

    [Fact]
    public void SkillManager_IsAssignableTo_IPromptSectionContributor()
    {
        IPromptSectionContributor contributor = CreateManager();
        contributor.SectionName.Should().Be(PromptSectionNames.Skills);
        contributor.Order.Should().Be(200);
    }

    [Fact]
    public async Task BuildAsync_WithNoSkills_ReturnsPlaceholder()
    {
        var manager = CreateManager();
        var result = await manager.BuildAsync(new PromptContext(), TestContext.Current.CancellationToken);
        result.Should().Be("暂无可用技能。");
    }
}
