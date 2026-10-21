using FluentAssertions;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Prompts;
using Seeing.Agent.Core.Prompts;
using System.Text.Json;
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
        var low = new FakeContributor(PromptSectionNames.Tools, order: 10, "LOW", "## Tools");
        var high = new FakeContributor(PromptSectionNames.Tools, order: 50, "HIGH", "## Tools");
        var skills = new FakeContributor(PromptSectionNames.Skills, order: 200, "SKILL-BODY", "## Skills");

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

        var result = await builder.BuildAsync(context, TestContext.Current.CancellationToken);

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
    public async Task BuildAsync_ShouldNotAppendTools_WhenAnchorMissing()
    {
        // Tools 已由 API tools 字段承载；真实贡献者在模板无 ## Tools 锚点时自述返回 null（不兜底追加），
        // 该"避免冗余"保护从旧 builder 特例上收至 ToolsPromptSectionContributor 自身。
        var builder = new PromptBuilder([new ToolsPromptSectionContributor()]);
        var context = new PromptContext
        {
            Agent = new AgentDefinition
            {
                SystemPrompt = "No anchors here."
            },
            Tools = [new FunctionSchema { Name = "read", Description = "Read a file" }]
        };

        var result = await builder.BuildAsync(context, TestContext.Current.CancellationToken);

        result.Should().Be("No anchors here.");
        result.Should().NotContain("## Tools");
        result.Should().NotContain("read");
    }

    [Fact]
    public async Task BuildAsync_ShouldAppendAnySectionPerPlacement_WhenAnchorMissing()
    {
        // builder 不认识具体分节名：即便是 tools 分节，只要声明 AtEnd 且模板无锚点，同样按声明追加。
        var tools = new FakeContributor(PromptSectionNames.Tools, order: 1, "TOOLS-BODY", "## Tools");
        var builder = new PromptBuilder([tools]);
        var context = new PromptContext
        {
            Agent = new AgentDefinition
            {
                SystemPrompt = "No anchors here."
            }
        };

        var result = await builder.BuildAsync(context, TestContext.Current.CancellationToken);

        result.Should().Contain("## Tools");
        result.Should().Contain("TOOLS-BODY");
    }

    [Fact]
    public async Task BuildAsync_ShouldAppendNonToolsSections_WhenAnchorsMissing_InStableOrder()
    {
        var tools = new FakeContributor(PromptSectionNames.Tools, order: 100, "TOOLS-BODY", "## Tools");
        var skills = new FakeContributor(PromptSectionNames.Skills, order: 200, "SKILLS-BODY", "## Skills");
        var agents = new FakeContributor(
            PromptSectionNames.Agents, order: 300, "AGENTS-BODY", "## Agents",
            PromptSectionPlacement.After(PromptSectionNames.Skills));
        var env = new FakeContributor(
            PromptSectionNames.Environment, order: 400, "ENV-BODY", "## Environment",
            PromptSectionPlacement.After(PromptSectionNames.Agents));

        // 故意乱序注册；builder 不认识分节名，按贡献者自述的 Order + Placement 追加
        // （tools 此处是 fake 贡献者，无锚点即按声明追加；真实 Tools 贡献者自带锚点门闸）
        var builder = new PromptBuilder([skills, env, tools, agents]);
        var context = new PromptContext
        {
            Agent = new AgentDefinition
            {
                SystemPrompt = "Base prompt without any anchors."
            }
        };

        var result = await builder.BuildAsync(context, TestContext.Current.CancellationToken);

        result.Should().Contain("## Tools");
        result.Should().Contain("TOOLS-BODY");

        var toolsIdx = result.IndexOf("## Tools", StringComparison.Ordinal);
        var skillsIdx = result.IndexOf("## Skills", StringComparison.Ordinal);
        var agentsIdx = result.IndexOf("## Agents", StringComparison.Ordinal);
        var envIdx = result.IndexOf("## Environment", StringComparison.Ordinal);

        toolsIdx.Should().BeGreaterThan(-1);
        toolsIdx.Should().BeLessThan(skillsIdx);
        skillsIdx.Should().BeGreaterThan(-1);
        skillsIdx.Should().BeLessThan(agentsIdx);
        agentsIdx.Should().BeLessThan(envIdx);
    }

    [Fact]
    public async Task BuildAsync_AppendedSkillsSection_ShouldExposeSkillNames_ForSkillToolLookup()
    {
        // 模拟 SkillManager.BuildAsync 的真实输出格式：H2 标题 + "### 技能名" + 描述。
        // 兜底注入后，模型应能从该节读到技能名，用于调用 skill 工具（SkillTool.ExecuteAsync 的 name 参数）。
        var skillsContent = """
            以下技能可供使用：

            ### schedule
            管理日程

            ### pdf
            处理 PDF 文档
            """;
        var skills = new FakeContributor(PromptSectionNames.Skills, order: 200, skillsContent, "## Skills");
        var builder = new PromptBuilder([skills]);
        var context = new PromptContext
        {
            Agent = new AgentDefinition
            {
                SystemPrompt = "You are an agent."
            }
        };

        var result = await builder.BuildAsync(context, TestContext.Current.CancellationToken);

        // H2 锚点 + 技能名同时出现，且技能名在 H2 之下
        var skillsHeadingIdx = result.IndexOf("## Skills", StringComparison.Ordinal);
        skillsHeadingIdx.Should().BeGreaterThan(-1);

        result.IndexOf("### schedule", StringComparison.Ordinal).Should().BeGreaterThan(skillsHeadingIdx);
        result.IndexOf("### pdf", StringComparison.Ordinal).Should().BeGreaterThan(skillsHeadingIdx);

        // 技能名格式与 SkillTool 的 name 参数一致（SkillManager.BuildAsync 以 "### {Name}" 呈现）
        result.Should().Contain("### schedule");
        result.Should().Contain("### pdf");
    }

    [Fact]
    public async Task BuildAsync_ShouldPreferAnchor_Injection_WhenAnchorExists()
    {
        // 有锚点时仍走精确注入（锚点下方），而非末尾追加
        var tools = new FakeContributor(PromptSectionNames.Tools, order: 100, "TOOLS-BODY", "## Tools");
        var builder = new PromptBuilder([tools]);
        var context = new PromptContext
        {
            Agent = new AgentDefinition
            {
                SystemPrompt = """
                Intro

                ## Tools

                ## Outro

                Tail
                """
            }
        };

        var result = await builder.BuildAsync(context, TestContext.Current.CancellationToken);

        result.Should().Contain("## Tools");
        result.Should().Contain("TOOLS-BODY");
        // TOOLS-BODY 注入在 ## Tools 与下一个 H2(## Outro) 之间
        var toolsIdx = result.IndexOf("## Tools", StringComparison.Ordinal);
        var outroIdx = result.IndexOf("## Outro", StringComparison.Ordinal);
        toolsIdx.Should().BeGreaterThan(-1);
        outroIdx.Should().BeGreaterThan(-1);
        result.IndexOf("TOOLS-BODY", StringComparison.Ordinal).Should().BeGreaterThan(toolsIdx);
        result.IndexOf("TOOLS-BODY", StringComparison.Ordinal).Should().BeLessThan(outroIdx);
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

        var result = await builder.BuildAsync(context, TestContext.Current.CancellationToken);

        result.Should().Contain("## Tools");
        result.Should().Contain("### read");
        result.Should().Contain("Read a file");
    }

    [Fact]
    public async Task ToolsContributor_ShouldNotExpandParameterSchema_InSystemPrompt()
    {
        // 完整 schema 由 API tools 参数承载；system 内仅名称+描述，压缩体积并稳定厂商侧缓存前缀
        var parametersJson = @"{
            ""type"": ""object"",
            ""properties"": {
                ""path"": { ""type"": ""string"", ""description"": ""文件路径"" }
            },
            ""required"": [""path""]
        }";

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
                    Description = "Read a file",
                    Parameters = JsonDocument.Parse(parametersJson).RootElement
                }
            ]
        };

        var result = await builder.BuildAsync(context, TestContext.Current.CancellationToken);

        result.Should().Contain("### read");
        result.Should().Contain("Read a file");
        result.Should().NotContain("`path`");
        result.Should().NotContain("(必需)");
    }

    [Fact]
    public async Task EnvironmentContributor_ShouldNotEmbedModelName()
    {
        // 模型名由 API 请求的 model 字段承载；写进 system 会因换模型破坏厂商侧前缀缓存
        var builder = new PromptBuilder([new EnvironmentPromptSectionContributor()]);
        var context = new PromptContext
        {
            Agent = new AgentDefinition
            {
                SystemPrompt = "## Environment\n"
            },
            ModelName = "anthropic/claude-sonnet",
            WorkingDirectory = "C:\\repo",
            Timestamp = new DateTime(2026, 9, 9, 10, 0, 0)
        };

        var result = await builder.BuildAsync(context, TestContext.Current.CancellationToken);

        result.Should().Contain("Working directory");
        result.Should().Contain("Today's date");
        result.Should().NotContain("Model:");
        result.Should().NotContain("claude-sonnet");
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

        var result = await builder.BuildAsync(context, TestContext.Current.CancellationToken);

        result.Should().Contain("**explore**");
        result.Should().Contain("Explore the codebase");
        result.Should().NotContain("**build**");
    }

    private sealed class FakeContributor : IPromptSectionContributor
    {
        private readonly string _body;

        public FakeContributor(
            string sectionName,
            int order,
            string body,
            string heading = "## Section",
            PromptSectionPlacement? placement = null)
        {
            SectionName = sectionName;
            Order = order;
            _body = body;
            Heading = heading;
            Placement = placement ?? PromptSectionPlacement.AtEnd;
        }

        public string SectionName { get; }
        public string Heading { get; }
        public PromptSectionPlacement Placement { get; }
        public int Order { get; }

        public Task<string?> BuildAsync(PromptContext context, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(_body);
    }
}
