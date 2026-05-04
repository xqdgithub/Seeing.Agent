using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Llm;
using Seeing.Agent.Tools;
using Seeing.Agent.Tools.Attributes;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Tests.Tools
{
    /// <summary>
    /// AgentMode 基于 Tool 的过滤测试
    /// </summary>
    public class AgentModeFilterTests
    {
        private readonly Mock<ILogger<ToolInvoker>> _loggerMock;
        private readonly Mock<ILogger<HookManager>> _hookLoggerMock;
        private readonly HookManager _hookManager;

        public AgentModeFilterTests()
        {
            _loggerMock = new Mock<ILogger<ToolInvoker>>();
            _hookLoggerMock = new Mock<ILogger<HookManager>>();
            _hookManager = new HookManager(_hookLoggerMock.Object);
        }

        [Fact]
        public void PrimaryMode_ShouldIncludePrimaryAndSubAgentTools()
        {
            var invoker = new ToolInvoker(_loggerMock.Object, _hookManager);
            invoker.RegisterToolsFromType(typeof(PrimaryTools));

            var schemas = invoker.GetToolSchemasForMode(AgentMode.Primary);

            schemas.Should().HaveCount(10);
            var ids = schemas.Select(s => s.Function.Name).ToList();
            ids.Should().Contain(new[] { "write", "edit", "bash", "question", "plan_enter", "read", "grep", "glob", "webfetch", "websearch" });
        }

        [Fact]
        public void SubAgentMode_ShouldOnlyIncludeSubAgentTools()
        {
            var invoker = new ToolInvoker(_loggerMock.Object, _hookManager);
            invoker.RegisterToolsFromType(typeof(PrimaryTools));

            var subSchemas = invoker.GetToolSchemasForMode(AgentMode.SubAgent);
            subSchemas.Should().HaveCount(5);
            var ids = subSchemas.Select(s => s.Function.Name).ToList();
            ids.Should().Contain(new[] { "read", "grep", "glob", "webfetch", "websearch" });
            ids.Should().NotContain(new[] { "write", "edit", "bash", "question", "plan_enter" });
        }

        [Fact]
        public void AllMode_ShouldIncludeAllTools()
        {
            var invoker = new ToolInvoker(_loggerMock.Object, _hookManager);
            invoker.RegisterToolsFromType(typeof(PrimaryTools));

            var allSchemas = invoker.GetToolSchemasForMode(AgentMode.All);
            allSchemas.Should().HaveCount(10);
        }

        [Fact]
        public void Default_ShouldBePrimary()
        {
            var invoker = new ToolInvoker(_loggerMock.Object, _hookManager);
            invoker.RegisterToolsFromType(typeof(PrimaryTools));

            var defaultSchemas = invoker.GetToolSchemasForMode();
            var primarySchemas = invoker.GetToolSchemasForMode(AgentMode.Primary);
            defaultSchemas.Should().BeEquivalentTo(primarySchemas);
        }
    }

    public static class PrimaryTools
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
    }
}
