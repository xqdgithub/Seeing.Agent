using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Core.Tools;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Tests.Tools
{
    /// <summary>
    /// ToolManager schema 结算：先求交集后生成、稳定排序、缓存失效。
    /// </summary>
    public class ToolManagerSchemaSettlementTests
    {
        private readonly Mock<ILogger<ToolManager>> _loggerMock = new();
        private readonly Mock<IHookManager> _hookManagerMock = new();
        private int _triggerCount;
        private long _hookVersion;

        public ToolManagerSchemaSettlementTests()
        {
            _hookManagerMock
                .SetupGet(h => h.Version)
                .Returns(() => _hookVersion);

            _hookManagerMock
                .Setup(h => h.TriggerBlockingAsync(
                    It.IsAny<HookSpec>(),
                    It.IsAny<string>(),
                    It.IsAny<IReadOnlyDictionary<string, object?>>(),
                    It.IsAny<IDictionary<string, object?>>(),
                    It.IsAny<CancellationToken>()))
                .Callback<HookSpec, string, IReadOnlyDictionary<string, object?>, IDictionary<string, object?>, CancellationToken>(
                    (_, _, _, _, _) => _triggerCount++)
                .ReturnsAsync(HookResult.Success);
        }

        private int TriggerCount => _triggerCount;

        private ToolManager CreateManager() => new(_loggerMock.Object, _hookManagerMock.Object);

        private static AgentDefinition Agent(params string[] denied) => new()
        {
            Name = "test",
            DeniedTools = denied.ToList()
        };

        [Fact]
        public async Task GetToolSchemasAsync_WithSubsetSettled_ShouldOnlyGenerateSettledTools()
        {
            // Arrange
            using var manager = CreateManager();
            for (var i = 1; i <= 5; i++)
                await manager.RegisterToolAsync(new StubTool($"tool{i}"));

            var baseline = TriggerCount;

            // Act
            var schemas = await manager.GetToolSchemasAsync(
                new[] { "tool2", "tool4" }, Agent(), TestContext.Current.CancellationToken);

            // Assert
            schemas.Should().HaveCount(2);
            schemas.Select(s => s.Function!.Name).Should().BeEquivalentTo(new[] { "tool2", "tool4" });
            (TriggerCount - baseline).Should().Be(2);
        }

        [Fact]
        public async Task GetToolSchemasAsync_ShouldApplyAgentDeniedBeforeGeneration()
        {
            // Arrange
            using var manager = CreateManager();
            for (var i = 1; i <= 3; i++)
                await manager.RegisterToolAsync(new StubTool($"tool{i}"));

            var baseline = TriggerCount;

            // Act
            var schemas = await manager.GetToolSchemasAsync(
                new[] { "tool1", "tool2", "tool3" }, Agent("tool2"), TestContext.Current.CancellationToken);

            // Assert
            schemas.Should().HaveCount(2);
            schemas.Select(s => s.Function!.Name).Should().BeEquivalentTo(new[] { "tool1", "tool3" });
            (TriggerCount - baseline).Should().Be(2);
        }

        [Fact]
        public async Task GetToolSchemasAsync_ShouldReturnStableOrder()
        {
            // Arrange
            using var manager = CreateManager();
            await manager.RegisterToolAsync(new StubTool("z"));
            await manager.RegisterToolAsync(new StubTool("a"));
            await manager.RegisterToolAsync(new StubTool("m"));

            var settled = new[] { "m", "z", "a" };

            // Act
            var first = await manager.GetToolSchemasAsync(settled, Agent(), TestContext.Current.CancellationToken);
            var second = await manager.GetToolSchemasAsync(settled, Agent(), TestContext.Current.CancellationToken);

            // Assert
            first.Select(s => s.Function!.Name).Should().ContainInOrder("a", "m", "z");
            second.Select(s => s.Function!.Name).Should().ContainInOrder("a", "m", "z");
        }

        [Fact]
        public async Task GetToolSchemasAsync_WithSameKey_ShouldHitCache()
        {
            // Arrange
            using var manager = CreateManager();
            await manager.RegisterToolAsync(new StubTool("cache_a"));
            await manager.RegisterToolAsync(new StubTool("cache_b"));

            var settled = new[] { "cache_a", "cache_b" };

            // Act
            await manager.GetToolSchemasAsync(settled, Agent(), TestContext.Current.CancellationToken);
            var baseline = TriggerCount;
            var cached = await manager.GetToolSchemasAsync(settled, Agent(), TestContext.Current.CancellationToken);

            // Assert
            cached.Should().HaveCount(2);
            (TriggerCount - baseline).Should().Be(0);
        }

        [Fact]
        public async Task GetToolSchemasAsync_AfterHookVersionChanged_ShouldInvalidateCache()
        {
            // Arrange
            using var manager = CreateManager();
            await manager.RegisterToolAsync(new StubTool("hook_a"));
            var settled = new[] { "hook_a" };

            // Act
            await manager.GetToolSchemasAsync(settled, Agent(), TestContext.Current.CancellationToken);
            var baseline = TriggerCount;

            _hookVersion = 1;
            await manager.GetToolSchemasAsync(settled, Agent(), TestContext.Current.CancellationToken);

            // Assert
            (TriggerCount - baseline).Should().Be(1);
        }

        [Fact]
        public async Task GetToolSchemasAsync_UncacheableTool_ShouldNotCache()
        {
            // Arrange
            using var manager = CreateManager();
            await manager.RegisterToolAsync(new StubTool(
                "uncacheable",
                new Dictionary<string, string> { [ToolCapabilityKeys.Cacheable] = "false" }));

            var settled = new[] { "uncacheable" };

            // Act
            await manager.GetToolSchemasAsync(settled, Agent(), TestContext.Current.CancellationToken);
            var baseline = TriggerCount;
            await manager.GetToolSchemasAsync(settled, Agent(), TestContext.Current.CancellationToken);

            // Assert
            (TriggerCount - baseline).Should().Be(1);
        }

        private sealed class StubTool : ITool
        {
            public StubTool(string id, IReadOnlyDictionary<string, string>? capabilities = null)
            {
                Id = id;
                _capabilities = capabilities;
            }

            private readonly IReadOnlyDictionary<string, string>? _capabilities;

            public IReadOnlyDictionary<string, string>? Capabilities => _capabilities;

            public string Id { get; }

            public string Description => $"工具 {Id}";

            public IReadOnlyList<string> Tags => Array.Empty<string>();

            public ToolCategory Category => ToolCategory.General;

            public JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new { type = "object" });

            public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
                => Task.FromResult(new ToolResult { Success = true, Output = "ok" });
        }
    }
}
