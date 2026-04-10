using FluentAssertions;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Core.Prompts;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Tests.Prompts
{
    /// <summary>
    /// DynamicPromptBuilder 测试
    /// </summary>
    public class DynamicPromptBuilderTests
    {
        private readonly DynamicPromptBuilder _builder;

        public DynamicPromptBuilderTests()
        {
            _builder = new DynamicPromptBuilder();
        }

        [Fact]
        public void Build_WithEmptyPrompt_ReturnsEmptyString()
        {
            var context = new PromptContext();
            var result = _builder.Build(string.Empty, context);
            result.Should().BeEmpty();
        }

        [Fact]
        public void Build_WithToolsPlaceholder_ReplacesWithToolSection()
        {
            var basePrompt = "You are an assistant.\n\n{{tools}}\n\nPlease help the user.";
            var context = new PromptContext
            {
                Tools = new[]
                {
                    new FunctionSchema { Name = "read", Description = "读取文件内容" },
                    new FunctionSchema { Name = "write", Description = "写入文件内容" }
                }
            };

            var result = _builder.Build(basePrompt, context);

            result.Should().Contain("可用工具");
            result.Should().Contain("read");
            result.Should().Contain("write");
            result.Should().NotContain("{{tools}}");
        }

        [Fact]
        public void Build_WithAgentsPlaceholder_ReplacesWithAgentSection()
        {
            var basePrompt = "Available agents:\n\n{{agents}}";
            var context = new PromptContext
            {
                Agents = new[]
                {
                    new AgentInfo { Name = "build", Description = "主要构建代理", Mode = AgentMode.Primary },
                    new AgentInfo { Name = "explore", Description = "代码库探索代理", Mode = AgentMode.SubAgent }
                }
            };

            var result = _builder.Build(basePrompt, context);

            result.Should().Contain("可用代理");
            result.Should().Contain("build");
            result.Should().Contain("explore");
            result.Should().NotContain("{{agents}}");
        }

        [Fact]
        public void Build_WithSkillsPlaceholder_ReplacesWithSkillSection()
        {
            var basePrompt = "Skills available:\n\n{{skills}}";
            var context = new PromptContext
            {
                Skills = new[]
                {
                    new SkillInfo { Name = "code-review", Description = "代码审查技能" },
                    new SkillInfo { Name = "refactor", Description = "重构技能" }
                }
            };

            var result = _builder.Build(basePrompt, context);

            result.Should().Contain("可用技能");
            result.Should().Contain("code-review");
            result.Should().Contain("refactor");
            result.Should().NotContain("{{skills}}");
        }

        [Fact]
        public void Build_WithCustomVariables_ReplacesCorrectly()
        {
            var basePrompt = "Hello {{user_name}}, your project is {{project_name}}.";
            var context = new PromptContext
            {
                Variables = new Dictionary<string, string>
                {
                    ["user_name"] = "Alice",
                    ["project_name"] = "MyApp"
                }
            };

            var result = _builder.Build(basePrompt, context);

            result.Should().Contain("Hello Alice");
            result.Should().Contain("your project is MyApp");
            result.Should().NotContain("{{user_name}}");
            result.Should().NotContain("{{project_name}}");
        }

        [Fact]
        public void BuildToolSection_WithEmptyTools_ReturnsEmptyMessage()
        {
            var result = _builder.BuildToolSection(Array.Empty<FunctionSchema>());
            result.Should().Contain("暂无可用工具");
        }

        [Fact]
        public void BuildToolSection_WithTools_FormatsCorrectly()
        {
            var tools = new[]
            {
                new FunctionSchema { Name = "bash", Description = "执行 shell 命令" },
                new FunctionSchema { Name = "grep", Description = "搜索文件内容" }
            };

            var result = _builder.BuildToolSection(tools);

            result.Should().Contain("可用工具");
            result.Should().Contain("bash");
            result.Should().Contain("执行 shell 命令");
            result.Should().Contain("grep");
            result.Should().Contain("搜索文件内容");
        }

        [Fact]
        public void BuildAgentSection_WithEmptyAgents_ReturnsEmptyMessage()
        {
            var result = _builder.BuildAgentSection(Array.Empty<AgentInfo>());
            result.Should().Contain("暂无可用代理");
        }

        [Fact]
        public void BuildAgentSection_WithAgents_GroupedByMode()
        {
            var agents = new[]
            {
                new AgentInfo { Name = "primary", Description = "主代理描述", Mode = AgentMode.Primary },
                new AgentInfo { Name = "sub", Description = "子代理描述", Mode = AgentMode.SubAgent },
                new AgentInfo { Name = "all", Description = "通用代理描述", Mode = AgentMode.All }
            };

            var result = _builder.BuildAgentSection(agents);

            result.Should().Contain("主代理");
            result.Should().Contain("子代理");
            result.Should().Contain("primary");
            result.Should().Contain("sub");
            result.Should().Contain("all");
        }

        [Fact]
        public void BuildSkillSection_WithEmptySkills_ReturnsEmptyMessage()
        {
            var result = _builder.BuildSkillSection(Array.Empty<SkillInfo>());
            result.Should().Contain("暂无可用技能");
        }

        [Fact]
        public void BuildSkillSection_WithSkills_FormatsCorrectly()
        {
            var skills = new[]
            {
                new SkillInfo
                {
                    Name = "test-skill",
                    Description = "测试技能描述",
                    Tags = new List<string> { "test", "demo" }
                }
            };

            var result = _builder.BuildSkillSection(skills);

            result.Should().Contain("可用技能");
            result.Should().Contain("test-skill");
            result.Should().Contain("测试技能描述");
            result.Should().Contain("test, demo");
        }

        [Fact]
        public void Build_WithBuiltinVariables_ReplacesCorrectly()
        {
            var basePrompt = "Model: {{model}}, Session: {{session_id}}, Time: {{timestamp}}";
            var context = new PromptContext
            {
                ModelName = "gpt-4",
                SessionId = "test-session-123",
                Timestamp = new DateTime(2024, 1, 15, 10, 30, 0)
            };

            var result = _builder.Build(basePrompt, context);

            result.Should().Contain("Model: gpt-4");
            result.Should().Contain("Session: test-session-123");
            result.Should().Contain("Time: 2024-01-15 10:30:00");
        }

        [Fact]
        public void Build_WithMultiplePlaceholders_ReplacesAll()
        {
            var basePrompt = @"
# System Prompt

{{tools}}

{{agents}}

{{skills}}

Current model: {{model}}
";

            var context = new PromptContext
            {
                Tools = new[] { new FunctionSchema { Name = "tool1", Description = "desc1" } },
                Agents = new[] { new AgentInfo { Name = "agent1", Description = "desc1", Mode = AgentMode.Primary } },
                Skills = new[] { new SkillInfo { Name = "skill1", Description = "desc1" } },
                ModelName = "test-model"
            };

            var result = _builder.Build(basePrompt, context);

            result.Should().Contain("可用工具");
            result.Should().Contain("可用代理");
            result.Should().Contain("可用技能");
            result.Should().Contain("test-model");
            result.Should().NotContain("{{tools}}");
            result.Should().NotContain("{{agents}}");
            result.Should().NotContain("{{skills}}");
            result.Should().NotContain("{{model}}");
        }

        [Fact]
        public void BuildToolSection_WithParameters_FormatsParameters()
        {
            var parametersJson = @"{
                ""type"": ""object"",
                ""properties"": {
                    ""path"": {
                        ""type"": ""string"",
                        ""description"": ""文件路径""
                    },
                    ""content"": {
                        ""type"": ""string"",
                        ""description"": ""写入内容"",
                        ""required"": true
                    }
                }
            }";

            var tools = new[]
            {
                new FunctionSchema
                {
                    Name = "write",
                    Description = "写入文件",
                    Parameters = JsonDocument.Parse(parametersJson).RootElement
                }
            };

            var result = _builder.BuildToolSection(tools);

            result.Should().Contain("write");
            result.Should().Contain("参数");
            result.Should().Contain("path");
            result.Should().Contain("content");
        }
    }
}