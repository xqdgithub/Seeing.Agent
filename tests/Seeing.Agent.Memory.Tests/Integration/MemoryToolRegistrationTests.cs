using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Memory.Extensions;
using Seeing.Agent.Memory.Integration.Tools;
using Xunit;

namespace Seeing.Agent.Memory.Tests.Integration;

public class MemoryToolRegistrationTests
{
    [Fact]
    public void TryAddSingleton_ITool_IsSkippedWhenAlreadyRegistered()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITool>(new StubTool("read"));
        services.TryAddSingleton<ITool>(new StubTool("memory_search"));

        services.Where(d => d.ServiceType == typeof(ITool)).Should().HaveCount(1,
            "TryAddSingleton<ITool> must not be used after built-in tools are registered");
    }

    [Fact]
    public void AddMemoryServices_AfterBuiltInITool_ShouldRegisterMemorySearchDescriptor()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITool>(new StubTool("read"));
        services.AddMemoryServices("Data Source=:memory:");

        services.Should().Contain(d => d.ServiceType == typeof(MemorySearchTool));
        services.Should().Contain(d => d.ServiceType == typeof(MemoryWriteTool));
        services.Should().Contain(d => d.ServiceType == typeof(MemoryReadTool));
        services.Where(d => d.ServiceType == typeof(ITool)).Should().HaveCountGreaterThan(3);
    }

    private sealed class StubTool : ITool
    {
        public StubTool(string id) => Id = id;
        public string Id { get; }
        public string Description => "stub";
        public JsonElement ParametersSchema => default;

        public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context) =>
            throw new NotSupportedException();
    }
}
