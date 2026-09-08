using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Hosting.Execution;
using Seeing.Agent.Core.Modules;
using Seeing.Agent.Core.Tools;
using Seeing.Session.Core;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Invariants.Tests.Invariants;

/// <summary>
/// Spec §8：可见即可请求 — schema 工具 id == 层1∩层2（再经层3 Agent Deny）；
/// Ask 可进 schema；Deny / 未启用 / 禁用不得进 schema。
/// </summary>
public class VisibilityTests
{
    [Fact]
    public async Task Schema_ToolIds_Should_Equal_Layer1_Intersect_Layer2_Minus_AgentDeny()
    {
        var catalog = CreateCatalog(
            processEnabled: ["filesystem", "git", "memory"],
            modules:
            [
                Desc("filesystem", ["read", "write"]),
                Desc("git", ["git_status"]),
                Desc("memory", ["memory_search"])
            ]);

        var session = SessionData.Create(scenario: "code");
        var settlement = SessionSettlement.Compute(
            session,
            catalog,
            processScenario: "full",
            userToolsDisabled: null);

        // code scenario ∩ process enabled：filesystem+git，不含 memory
        settlement.SettledToolIds.Should().BeEquivalentTo(["git_status", "read", "write"]);

        var manager = CreateToolManager(["read", "write", "git_status", "memory_search"]);
        var agent = new AgentDefinition
        {
            Name = "t",
            DeniedTools = ["write"], // 层3
            PermissionRules =
            [
                new PermissionRuleEntry
                {
                    Kind = PermissionKind.Tool,
                    Pattern = "read",
                    Effect = PermissionEffect.Ask,
                    Priority = 10
                },
                PermissionRuleEntry.Deny(PermissionKind.Tool, "git_status", priority: 10)
            ]
        };

        var schemas = await manager.GetToolSchemasAsync(settlement.SettledToolIds, agent);
        var schemaIds = schemas.Select(s => s.Function!.Name).OrderBy(x => x).ToArray();

        // 层1∩层2 − Agent DeniedTools；Ask 仍可出现；Permission Deny 属层4（执行时），不从 schema 剔除
        schemaIds.Should().Equal("git_status", "read");
        schemaIds.Should().Contain("read", "Ask 权限仍可进入 schema");
        schemaIds.Should().NotContain("write");
        schemaIds.Should().NotContain("memory_search");
    }

    [Fact]
    public async Task Disabled_And_NotEnabled_Tools_Must_Not_Enter_Schema()
    {
        var catalog = CreateCatalog(
            processEnabled: ["filesystem", "git"],
            modules:
            [
                Desc("filesystem", ["read", "write"]),
                Desc("git", ["git_status", "git_commit"]),
                Desc("memory", ["memory_search"]) // 未启用
            ]);

        var session = SessionData.Create(scenario: "code");
        var settlement = SessionSettlement.Compute(
            session,
            catalog,
            processScenario: "full",
            userToolsDisabled: ["write", "git_commit"]);

        settlement.SettledToolIds.Should().BeEquivalentTo(["git_status", "read"]);
        settlement.SettledToolIds.Should().NotContain("write");
        settlement.SettledToolIds.Should().NotContain("git_commit");
        settlement.SettledToolIds.Should().NotContain("memory_search");

        var manager = CreateToolManager(["read", "write", "git_status", "git_commit", "memory_search"]);
        var agent = new AgentDefinition { Name = "t" };
        var schemas = await manager.GetToolSchemasAsync(settlement.SettledToolIds, agent);
        var schemaIds = schemas.Select(s => s.Function!.Name).ToArray();

        schemaIds.Should().BeEquivalentTo(["git_status", "read"]);
        schemaIds.Should().NotContain(["write", "git_commit", "memory_search"]);
    }

    [Fact]
    public async Task Ask_Permission_Does_Not_Exclude_Tool_From_Schema()
    {
        var manager = CreateToolManager(["bash"]);
        var agent = new AgentDefinition
        {
            Name = "t",
            PermissionDefaultEffect = PermissionEffect.Ask,
            PermissionRules =
            [
                new PermissionRuleEntry
                {
                    Kind = PermissionKind.Tool,
                    Pattern = "bash",
                    Effect = PermissionEffect.Ask,
                    Priority = 0
                }
            ]
        };

        var schemas = await manager.GetToolSchemasAsync(["bash"], agent);
        schemas.Select(s => s.Function!.Name).Should().Equal("bash");
    }

    private static ToolManager CreateToolManager(IEnumerable<string> ids)
    {
        var hooks = new HookManager(NullLogger<HookManager>.Instance);
        var manager = new ToolManager(NullLogger<ToolManager>.Instance, hooks);
        foreach (var id in ids)
            manager.RegisterTool(new NamedTool(id));
        return manager;
    }

    private static ModuleCatalog CreateCatalog(
        IEnumerable<string> processEnabled,
        IReadOnlyList<ModuleDescriptor> modules)
    {
        var catalog = new ModuleCatalog();
        catalog.ReplaceAvailable(modules);
        catalog.ReplaceEnabled(processEnabled);
        return catalog;
    }

    private static ModuleDescriptor Desc(string id, string[] tools)
        => new(id, tools, Array.Empty<string>(), Array.Empty<string>());

    private sealed class NamedTool(string id) : ITool
    {
        public string Id { get; } = id;
        public string Description => id;
        public IReadOnlyList<string> Tags => Array.Empty<string>();
        public ToolCategory Category => ToolCategory.General;
        public JsonElement ParametersSchema =>
            JsonSerializer.SerializeToElement(new { type = "object", properties = new { } });

        public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context) =>
            Task.FromResult(ToolResult.Succeeded("ok"));
    }
}
