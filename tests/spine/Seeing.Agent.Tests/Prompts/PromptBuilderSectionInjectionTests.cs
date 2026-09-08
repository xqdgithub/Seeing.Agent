using FluentAssertions;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Prompts;
using Seeing.Agent.Core.Prompts;
using Xunit;

namespace Seeing.Agent.Tests.Prompts;

public class PromptBuilderSectionInjectionTests
{
    [Fact]
    public void PromptBuilder_ShouldNotReference_SkillManagerType()
    {
        var ctorParams = typeof(PromptBuilder)
            .GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType)
            .ToList();

        ctorParams.Should().NotContain(t => t.Name == "SkillManager" || t.Name == "ISkillManager");

        typeof(PromptBuilder).GetFields(
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Public)
            .Select(f => f.FieldType)
            .Should().NotContain(t => t.Name == "SkillManager" || t.Name == "ISkillManager");

        // 构造应聚合 IPromptSectionContributor
        ctorParams.Should().Contain(t =>
            t.IsGenericType &&
            t.GetGenericTypeDefinition() == typeof(IEnumerable<>) &&
            t.GetGenericArguments()[0] == typeof(IPromptSectionContributor));
    }

    [Fact]
    public async Task BuildAsync_ShouldInjectSections_UnderAnchorHeadings_OrderedByOrder()
    {
        var low = new FakeContributor(PromptSectionNames.Tools, order: 10, "LOW");
        var high = new FakeContributor(PromptSectionNames.Tools, order: 50, "HIGH");
        var skills = new FakeContributor(PromptSectionNames.Skills, order: 200, "SKILL-BODY");

        var builder = new PromptBuilder([low, high, skills]);
        var context = new PromptContext
        {
            Agent = new AgentDefinition
            {
                Name = "test",
                SystemPrompt = """
                Intro

                ## Tools

                ## Skills

                Outro
                """
            }
        };

        var result = await builder.BuildAsync(context);

        result.Should().Contain("## Tools");
        result.Should().Contain("## Skills");
        result.Should().NotContain("{{tools}}");
        result.Should().NotContain("{{skills}}");

        var toolsIdx = result.IndexOf("## Tools", StringComparison.Ordinal);
        var lowIdx = result.IndexOf("LOW", StringComparison.Ordinal);
        var highIdx = result.IndexOf("HIGH", StringComparison.Ordinal);
        var skillsHeadingIdx = result.IndexOf("## Skills", StringComparison.Ordinal);
        var skillBodyIdx = result.IndexOf("SKILL-BODY", StringComparison.Ordinal);

        toolsIdx.Should().BeLessThan(lowIdx);
        lowIdx.Should().BeLessThan(highIdx);
        highIdx.Should().BeLessThan(skillsHeadingIdx);
        skillsHeadingIdx.Should().BeLessThan(skillBodyIdx);
    }

    [Fact]
    public async Task BuildAsync_ShouldSkipSection_WhenAnchorMissing()
    {
        var tools = new FakeContributor(PromptSectionNames.Tools, order: 1, "SHOULD-NOT-APPEAR");
        var builder = new PromptBuilder([tools]);
        var context = new PromptContext
        {
            Agent = new AgentDefinition
            {
                SystemPrompt = "No anchors here."
            }
        };

        var result = await builder.BuildAsync(context);

        result.Should().Be("No anchors here.");
        result.Should().NotContain("SHOULD-NOT-APPEAR");
    }

    [Fact]
    public async Task ToolsContributor_ShouldRender_FromContextTools()
    {
        var builder = new PromptBuilder([new ToolsPromptSectionContributor()]);
        var context = new PromptContext
        {
            Agent = new AgentDefinition
            {
                SystemPrompt = "## Tools\n"
            },
            Tools =
            [
                new FunctionSchema
                {
                    Name = "read",
                    Description = "Read a file"
                }
            ]
        };

        var result = await builder.BuildAsync(context);

        result.Should().Contain("## Tools");
        result.Should().Contain("### read");
        result.Should().Contain("Read a file");
    }

    [Fact]
    public async Task AgentsContributor_ShouldListSubAgents()
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.GetAgentsAsync())
            .ReturnsAsync(
            [
                new AgentDefinition { Name = "build", Mode = AgentMode.All },
                new AgentDefinition
                {
                    Name = "explore",
                    Mode = AgentMode.SubAgent,
                    Description = "Explore the codebase. Fast."
                }
            ]);

        var builder = new PromptBuilder([new AgentsPromptSectionContributor(registry.Object)]);
        var context = new PromptContext
        {
            Agent = new AgentDefinition
            {
                Name = "build",
                SystemPrompt = "## Agents\n"
            }
        };

        var result = await builder.BuildAsync(context);

        result.Should().Contain("**explore**");
        result.Should().Contain("Explore the codebase");
        result.Should().NotContain("**build**");
    }

    private sealed class FakeContributor : IPromptSectionContributor
    {
        private readonly string _body;

        public FakeContributor(string sectionName, int order, string body)
        {
            SectionName = sectionName;
            Order = order;
            _body = body;
        }

        public string SectionName { get; }
        public int Order { get; }

        public Task<string?> BuildAsync(PromptContext context, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(_body);
    }
}
