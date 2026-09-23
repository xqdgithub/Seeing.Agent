using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Prompts;
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
    [Fact]
    public void SkillManager_IsAssignableTo_IPromptSectionContributor()
    {
        IPromptSectionContributor contributor = new SkillManager(NullLogger<SkillManager>.Instance);
        contributor.SectionName.Should().Be(PromptSectionNames.Skills);
        contributor.Order.Should().Be(200);
    }

    [Fact]
    public async Task BuildAsync_WithNoSkills_ReturnsPlaceholder()
    {
        var manager = new SkillManager(NullLogger<SkillManager>.Instance);
        var result = await manager.BuildAsync(new PromptContext(), TestContext.Current.CancellationToken);
        result.Should().Be("暂无可用技能。");
    }
}
