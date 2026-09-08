using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Configuration;
using Seeing.Agent.Extensions;
using Seeing.Agent.Tools;
using Xunit;

namespace Seeing.Agent.Tests.Invariants;

/// <summary>
/// Spec §8：Core 零工具 — 只 <c>AddSeeingCore()</c> 时不应装载任何 ITool；schema 对空结算集为空。
/// </summary>
public class CoreZeroToolsTests
{
    private static readonly string[] CapabilityToolIds =
    [
        "read", "write", "edit", "multiedit",
        "bash", "grep", "glob",
        "git_status", "git_diff", "git_log", "git_commit",
        "web_fetch", "web_search",
        "current_time", "todo_write", "task", "task_status",
        "skill"
    ];

    [Fact]
    public async Task AddSeeingCore_Only_Should_Not_Register_Capability_Tools_And_Empty_Settled_Schema_Is_Empty()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var registry = new ConfigSectionRegistry();
        services.AddSingleton<IConfigSectionRegistry>(registry);
        services.AddSeeingCore(registry);

        await using var sp = services.BuildServiceProvider();
        var tools = sp.GetRequiredService<IToolManager>();

        var ids = tools.GetTools().Select(t => t.Id).ToArray();

        ids.Should().NotContain(CapabilityToolIds);
        ids.Should().BeEmpty("AddSeeingCore alone must register zero ITool");

        var schemas = await tools.GetToolSchemasAsync(
            Array.Empty<string>(),
            new AgentDefinition { Name = "t" });

        schemas.Should().BeEmpty("空层1∩层2 结算集 → schema 必须为空");
    }

    [Fact]
    public void AddSeeingCore_Should_Not_Register_ReadTool_In_DI()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var registry = new ConfigSectionRegistry();
        services.AddSingleton<IConfigSectionRegistry>(registry);
        services.AddSeeingCore(registry);

        services.Where(d => d.ServiceType == typeof(ITool))
            .Should().BeEmpty("AddSeeingCore alone must not register any ITool");
    }
}
