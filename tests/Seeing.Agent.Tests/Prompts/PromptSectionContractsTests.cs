using FluentAssertions;
using Seeing.Agent.Abstractions.Prompts;
using Xunit;

namespace Seeing.Agent.Tests.Prompts;

public class PromptSectionContractsTests
{
    [Fact]
    public void PromptSectionNames_ShouldDefineCoreSections()
    {
        PromptSectionNames.Tools.Should().Be("tools");
        PromptSectionNames.Skills.Should().Be("skills");
        PromptSectionNames.Agents.Should().Be("agents");
        PromptSectionNames.Environment.Should().Be("environment");
    }

    [Fact]
    public void PromptContext_ShouldExposeExpectedProperties()
    {
        var type = typeof(PromptContext);

        type.GetProperty(nameof(PromptContext.Tools))!.PropertyType
            .Should().Be(typeof(IEnumerable<Seeing.Agent.Abstractions.Llm.FunctionSchema>));
        type.GetProperty(nameof(PromptContext.Agents))!.PropertyType
            .Should().Be(typeof(IEnumerable<Seeing.Agent.Abstractions.Agents.AgentDefinition>));
        type.GetProperty(nameof(PromptContext.Skills))!.PropertyType
            .Should().Be(typeof(IEnumerable<Seeing.Agent.Abstractions.Skills.SkillInfo>));
        type.GetProperty(nameof(PromptContext.Agent))!.PropertyType
            .Should().Be(typeof(Seeing.Agent.Abstractions.Agents.AgentDefinition));
        type.GetProperty(nameof(PromptContext.Variables))!.PropertyType
            .Should().Be(typeof(Dictionary<string, string>));
    }

    [Fact]
    public void IPromptSectionContributor_ShouldDeclareSectionOrderAndBuildAsync()
    {
        var type = typeof(IPromptSectionContributor);

        type.GetProperty(nameof(IPromptSectionContributor.SectionName))!.PropertyType
            .Should().Be(typeof(string));
        type.GetProperty(nameof(IPromptSectionContributor.Order))!.PropertyType
            .Should().Be(typeof(int));

        var method = type.GetMethod(nameof(IPromptSectionContributor.BuildAsync));
        method.Should().NotBeNull();
        method!.ReturnType.Should().Be(typeof(Task<string?>));
        method.GetParameters().Should().HaveCount(2);
        method.GetParameters()[0].ParameterType.Should().Be(typeof(PromptContext));
        method.GetParameters()[1].ParameterType.Should().Be(typeof(CancellationToken));
    }
}
