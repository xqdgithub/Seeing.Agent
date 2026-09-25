using Seeing.Agent.Abstractions.Agents;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Core.Tools;
using Seeing.Agent.Abstractions.Tools;
using System.Reflection;
using Xunit;

namespace Seeing.Agent.Tests.Tools
{
    /// <summary>
    /// Agent 工具 Schema 筛选：Mode 不再硬编码工具集；DeniedTools 负责 SubAgent 禁 task。
    /// </summary>
    public class AgentModeFilterTests
    {
        private readonly Mock<ILogger<ToolManager>> _loggerMock;
        private readonly Mock<ILogger<HookManager>> _hookLoggerMock;
        private readonly HookManager _hookManager;

        public AgentModeFilterTests()
        {
            _loggerMock = new Mock<ILogger<ToolManager>>();
            _hookLoggerMock = new Mock<ILogger<HookManager>>();
            _hookManager = new HookManager(_hookLoggerMock.Object);
        }

        [Fact]
        public void ToolManager_ShouldNotHavePrimaryOrSubAgentHardcodedSets()
        {
            var type = typeof(ToolManager);
            var flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
            type.GetField("PrimaryTools", flags).Should().BeNull();
            type.GetField("SubAgentTools", flags).Should().BeNull();
            type.GetField("SubAgentExcludedTools", flags).Should().BeNull();
        }

        [Fact]
        public async Task GetToolSchemasForMode_ShouldReturnAllRegisteredTools_RegardlessOfMode()
        {
            var invoker = new ToolManager(_loggerMock.Object, _hookManager);
            await invoker.RegisterToolsFromTypeAsync(typeof(SampleTools));

            foreach (var mode in new[] { AgentMode.Primary, AgentMode.SubAgent, AgentMode.All })
            {
                var schemas = await invoker.GetToolSchemasForModeAsync(mode);
                schemas.Should().HaveCount(11);
                schemas.Select(s => s.Function.Name).Should().Contain("task");
            }
        }

        [Fact]
        public async Task GetToolSchemasForAgentAsync_SubAgentWithDeniedTask_ExcludesTask()
        {
            var invoker = new ToolManager(_loggerMock.Object, _hookManager);
            await invoker.RegisterToolsFromTypeAsync(typeof(SampleTools));

            var agent = new AgentDefinition
            {
                Name = "explore",
                Mode = AgentMode.SubAgent,
                DeniedTools = new List<string> { "task" }
            };

            var schemas = await invoker.GetToolSchemasForAgentAsync(agent);
            schemas.Should().HaveCount(10);
            schemas.Select(s => s.Function.Name).Should().NotContain("task");
            schemas.Select(s => s.Function.Name).Should().Contain(new[] { "read", "write", "bash" });
        }

        [Fact]
        public async Task Default_ShouldMatchPrimary_AllTools()
        {
            var invoker = new ToolManager(_loggerMock.Object, _hookManager);
            await invoker.RegisterToolsFromTypeAsync(typeof(SampleTools));

            var defaultSchemas = await invoker.GetToolSchemasForModeAsync();
            var primarySchemas = await invoker.GetToolSchemasForModeAsync(AgentMode.Primary);
            defaultSchemas.Should().BeEquivalentTo(primarySchemas);
        }
    }

    public static class SampleTools
    {
        [Tool("Write tool", Name = "write")]
        public static int Write(int v) => v;
        [Tool("Edit tool", Name = "edit")]
        public static int Edit(int v) => v;
        [Tool("Bash tool", Name = "bash")]
        public static int Bash(int v) => v;
        [Tool("Question tool", Name = "question")]
        public static int Question(int v) => v;
        [Tool("PlanEnter tool", Name = "plan_enter")]
        public static int PlanEnter(int v) => v;
        [Tool("Read tool", Name = "read")]
        public static string Read(string s) => s;
        [Tool("Grep tool", Name = "grep")]
        public static string Grep(string s) => s;
        [Tool("Glob tool", Name = "glob")]
        public static string Glob(string s) => s;
        [Tool("WebFetch tool", Name = "webfetch")]
        public static string WebFetch(string s) => s;
        [Tool("WebSearch tool", Name = "websearch")]
        public static string WebSearch(string s) => s;
        [Tool("Task tool", Name = "task")]
        public static int Task(int v) => v;
    }
}
