using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Core.Tools;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Tests.Tools;

public class ToolManagerUnregisterTests
{
    private sealed class StubTool(string id) : ITool
    {
        public string Id { get; } = id;
        public string Description => "stub";
        public IReadOnlyList<string> Tags => [];
        public ToolCategory Category => ToolCategory.General;
        public JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new { type = "object" });
        public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
            => Task.FromResult(new ToolResult { Success = true, Output = "ok" });
    }

    private static ToolManager CreateManager()
    {
        var hooks = new HookManager(NullLogger<HookManager>.Instance);
        return new ToolManager(NullLogger<ToolManager>.Instance, hooks);
    }

    [Fact]
    public void UnregisterTool_AfterRegister_RemovesFromGetTools()
    {
        var manager = CreateManager();
        manager.RegisterTool(new StubTool("demo"));
        manager.HasTool("demo").Should().BeTrue();

        var removed = manager.UnregisterTool("demo");

        removed.Should().BeTrue();
        manager.HasTool("demo").Should().BeFalse();
        manager.GetTools().Select(t => t.Id).Should().NotContain("demo");
    }

    [Fact]
    public void UnregisterTool_UnknownId_ReturnsFalse()
    {
        var manager = CreateManager();
        manager.UnregisterTool("missing").Should().BeFalse();
    }

    [Fact]
    public async Task UnregisterToolAsync_AfterRegister_RemovesFromGetTools()
    {
        var manager = CreateManager();
        await manager.RegisterToolAsync(new StubTool("async_demo"), TestContext.Current.CancellationToken);

        var removed = await manager.UnregisterToolAsync("async_demo", TestContext.Current.CancellationToken);

        removed.Should().BeTrue();
        manager.GetTools().Should().BeEmpty();
    }

    [Fact]
    public async Task UnregisterToolAsync_UnknownId_ReturnsFalse()
    {
        var manager = CreateManager();
        (await manager.UnregisterToolAsync("nope", TestContext.Current.CancellationToken)).Should().BeFalse();
    }
}
