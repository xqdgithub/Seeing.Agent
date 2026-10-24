using FluentAssertions;
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
