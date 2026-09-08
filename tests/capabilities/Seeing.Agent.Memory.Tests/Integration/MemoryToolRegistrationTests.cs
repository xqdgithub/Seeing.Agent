using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Abstractions.Configuration;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
        var registry = new StubConfigSectionRegistry();
        services.AddSingleton<IConfigSectionRegistry>(registry);
        services.AddMemoryServices(registry, "Data Source=:memory:");

        services.Should().Contain(d => d.ServiceType == typeof(MemorySearchTool));
        services.Should().Contain(d => d.ServiceType == typeof(MemoryWriteTool));
        services.Should().Contain(d => d.ServiceType == typeof(MemoryReadTool));
        // W1+W2：工具以具体类型登记，由 MemoryModule.Activate 经 IToolManager 注册，不再 TryAdd ITool
        services.Where(d => d.ServiceType == typeof(ITool)).Should().HaveCount(1);
    }

    private sealed class StubConfigSectionRegistry : IConfigSectionRegistry
    {
        private readonly Dictionary<string, ConfigSectionMeta> _sections =
            new(StringComparer.OrdinalIgnoreCase);

        public void Register(ConfigSectionMeta meta) => _sections[meta.Key] = meta;
        public IReadOnlyCollection<ConfigSectionMeta> Sections => _sections.Values;
        public bool TryGet(string key, out ConfigSectionMeta meta) =>
            _sections.TryGetValue(key, out meta!);
    }

    private sealed class StubTool : ITool
    {
        public StubTool(string id) => Id = id;
        public string Id { get; }
        public string Description => "stub";
        public IReadOnlyList<string> Tags => Array.Empty<string>();
        public ToolCategory Category => ToolCategory.General;
        public JsonElement ParametersSchema => default;

        public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context) =>
            throw new NotSupportedException();
    }
}
