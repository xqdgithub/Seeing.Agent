using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Llm;
using Seeing.Agent.Tools;
using Seeing.Agent.Tools.Attributes;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Tests.Tools
{
    /// <summary>
    /// ToolInvoker 单元测试
    /// </summary>
    public class ToolInvokerTests
    {
        private readonly Mock<ILogger<ToolInvoker>> _loggerMock;
        private readonly Mock<ILogger<HookManager>> _hookLoggerMock;
        private readonly HookManager _hookManager;

        public ToolInvokerTests()
        {
            _loggerMock = new Mock<ILogger<ToolInvoker>>();
            _hookLoggerMock = new Mock<ILogger<HookManager>>();
            _hookManager = new HookManager(_hookLoggerMock.Object);
        }

        [Fact]
        public void RegisterTool_ShouldAddTool()
        {
            var invoker = new ToolInvoker(_loggerMock.Object, _hookManager);
            var tool = new TestTool();

            invoker.RegisterTool(tool);

            invoker.HasTool("test_tool").Should().BeTrue();
        }

        [Fact]
        public void RegisterToolsFromType_ShouldDiscoverAnnotatedMethods()
        {
            var invoker = new ToolInvoker(_loggerMock.Object, _hookManager);

            invoker.RegisterToolsFromType(typeof(TestToolClass));

            invoker.HasTool("Add").Should().BeTrue();
            invoker.HasTool("greet").Should().BeTrue();
        }

        [Fact]
        public async Task ExecuteAsync_ShouldCallToolAndReturnResult()
        {
            var invoker = new ToolInvoker(_loggerMock.Object, _hookManager);
            invoker.RegisterToolsFromType(typeof(TestToolClass));

            var toolCall = new ToolCall
            {
                Id = "call-001",
                Function = new FunctionCall
                {
                    Name = "Add",
                    Arguments = JsonSerializer.Serialize(new { a = 5, b = 3 })
                }
            };

            var result = await invoker.ExecuteAsync(toolCall);

            result.Success.Should().BeTrue();
            result.Output.Should().Be("8");
        }

        [Fact]
        public async Task ExecuteAsync_ShouldHandleMissingTool()
        {
            var invoker = new ToolInvoker(_loggerMock.Object, _hookManager);

            var toolCall = new ToolCall
            {
                Id = "call-001",
                Function = new FunctionCall
                {
                    Name = "nonexistent",
                    Arguments = "{}"
                }
            };

            var result = await invoker.ExecuteAsync(toolCall);

            result.Success.Should().BeFalse();
            result.Error?.Should().Contain("工具不存在");
        }

        [Fact]
        public async Task ExecuteAsync_WithDictionaryArgs_ShouldWork()
        {
            var invoker = new ToolInvoker(_loggerMock.Object, _hookManager);
            invoker.RegisterToolsFromType(typeof(TestToolClass));

            var result = await invoker.ExecuteAsync("Add", new Dictionary<string, object?> { ["a"] = 10, ["b"] = 20 });

            result.Success.Should().BeTrue();
        }

        [Fact]
        public void GetToolSchemas_ShouldReturnAllSchemas()
        {
            var invoker = new ToolInvoker(_loggerMock.Object, _hookManager);
            invoker.RegisterToolsFromType(typeof(TestToolClass));

            var schemas = invoker.GetToolSchemas();

            schemas.Should().HaveCount(2);
            schemas.Select(s => s.Function.Name).Should().Contain("Add", "greet");
        }

        [Fact]
        public async Task GetToolSchemasForAgentAsync_WithAllowedList_FiltersToAllowed()
        {
            // Arrange
            var invoker = new ToolInvoker(_loggerMock.Object, _hookManager);
            invoker.RegisterToolsFromType(typeof(TestToolClass));
            var agent = new AgentInfo
            {
                Name = "test",
                Mode = AgentMode.All,
                AllowedTools = new List<string> { "Add" }
            };

            // Act
            var schemas = await invoker.GetToolSchemasForAgentAsync(agent);

            // Assert
            schemas.Should().HaveCount(1);
            schemas[0].Function!.Name.Should().Be("Add");
        }

        [Fact]
        public async Task GetToolSchemasForAgentAsync_WithDeniedList_ExcludesDenied()
        {
            // Arrange
            var invoker = new ToolInvoker(_loggerMock.Object, _hookManager);
            invoker.RegisterToolsFromType(typeof(TestToolClass));
            var agent = new AgentInfo
            {
                Name = "test",
                Mode = AgentMode.All,
                DeniedTools = new List<string> { "greet" }
            };

            // Act
            var schemas = await invoker.GetToolSchemasForAgentAsync(agent);

            // Assert
            schemas.Select(s => s.Function!.Name).Should().Contain("Add");
            schemas.Select(s => s.Function!.Name).Should().NotContain("greet");
        }
    }

    /// <summary>
    /// 测试工具类
    /// </summary>
    public class TestToolClass
    {
        [Tool("两数相加")]
        public static int Add(
            [ToolParam("第一个数")] int a,
            [ToolParam("第二个数")] int b)
        {
            return a + b;
        }

        [Tool("打招呼", Name = "greet")]
        public static string Greet([ToolParam("名字")] string name)
        {
            return $"Hello, {name}!";
        }
    }

    /// <summary>
    /// 简单测试工具
    /// </summary>
    public class TestTool : Seeing.Agent.Core.Interfaces.ITool
    {
        public string Id => "test_tool";
        public string Description => "测试工具";
        public JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new { type = "object" });

        public async Task<Seeing.Agent.Core.Models.ToolResult> ExecuteAsync(JsonElement arguments, Seeing.Agent.Core.Interfaces.ToolContext context)
        {
            return new Seeing.Agent.Core.Models.ToolResult
            {
                Success = true,
                Output = "完成"
            };
        }
    }
}