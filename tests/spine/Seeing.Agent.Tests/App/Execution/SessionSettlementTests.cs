using FluentAssertions;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Core.Scenarios;
using Seeing.Agent.Hosting.Execution;
using Seeing.Agent.Core.Modules;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.App.Execution;

/// <summary>
/// 会话级结算：模块层 ∩ bootEnabled；工具层再扣 Tools.Disabled（两层分离）。
/// </summary>
public class SessionSettlementTests
{
    [Fact]
    public void Compute_CodeAndWorkSessions_InSameProcess_NarrowDistinctTools()
    {
        var catalog = CreateCatalog(
            processEnabled: ["git", "memory", "filesystem", "basic"],
            modules:
            [
                Desc("git", ["git_status", "git_diff"]),
                Desc("memory", ["memory_search", "memory_store"]),
                Desc("filesystem", ["read", "write"]),
                Desc("basic", ["current_time"])
            ]);

        var sessionA = SessionData.Create(scenario: "code");
        var sessionB = SessionData.Create(scenario: "work");

        var settleA = SessionSettlement.Compute(sessionA, catalog, processScenario: "full", userToolsDisabled: null);
        var settleB = SessionSettlement.Compute(sessionB, catalog, processScenario: "full", userToolsDisabled: null);

        settleA.EnabledModules.Should().Contain("git");
        settleA.EnabledModules.Should().NotContain("memory");
        settleA.SettledToolIds.Should().Contain("git_status");
        settleA.SettledToolIds.Should().NotContain("memory_search");

        settleB.EnabledModules.Should().Contain("memory");
        settleB.EnabledModules.Should().NotContain("git");
        settleB.SettledToolIds.Should().Contain("memory_search");
        settleB.SettledToolIds.Should().NotContain("git_status");
    }

    [Fact]
    public void Compute_BootStar_SessionFull_GetsWideTools()
    {
        // Boot=* → filesystem ∈ bootEnabled；session full → 含 filesystem 工具
        var catalog = CreateCatalog(
            processEnabled: ["io.local", "filesystem", "shell", "basic"],
            modules:
            [
                Desc("io.local", []),
                Desc("filesystem", ["read", "write"]),
                Desc("shell", ["bash"]),
                Desc("basic", ["current_time"]),
            ]);

        var session = SessionData.Create(scenario: "full");
        var settle = SessionSettlement.Compute(session, catalog, processScenario: "minimal", userToolsDisabled: null);

        settle.EnabledModules.Should().Contain("filesystem");
        settle.SettledToolIds.Should().Contain("read");
        settle.SettledToolIds.Should().Contain("write");
    }

    [Fact]
    public void Compute_BootMinimal_SessionFull_NoFilesystemTools()
    {
        // Boot=minimal → filesystem ∉ bootEnabled；即使 session Scenario=full 也不能越界
        var catalog = CreateCatalog(
            processEnabled: ["io.local", "basic"], // bootEnabled 无 filesystem
            modules:
            [
                Desc("io.local", []),
                Desc("basic", ["current_time"]),
                Desc("filesystem", ["read", "write"]), // available but not boot-enabled
            ]);

        var session = SessionData.Create(scenario: "full");
        var settle = SessionSettlement.Compute(session, catalog, processScenario: "minimal", userToolsDisabled: null);

        settle.EnabledModules.Should().NotContain("filesystem");
        settle.SettledToolIds.Should().NotContain("read");
        settle.SettledToolIds.Should().NotContain("write");
    }

    [Fact]
    public void Compute_ToolsDisabled_DropsToolNotModule()
    {
        var catalog = CreateCatalog(
            processEnabled: ["shell", "io.local"],
            modules:
            [
                Desc("io.local", []),
                Desc("shell", ["bash", "powershell"]),
            ]);

        var session = SessionData.Create(scenario: "code");
        var settle = SessionSettlement.Compute(
            session,
            catalog,
            processScenario: "full",
            userToolsDisabled: null,
            resolveScenario: _ => new ScenarioDefinition(
                Name: "code",
                Modules: ["shell", "io.local"],
                DefaultAgent: "build",
                Seams: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ToolsDisabled: ["bash"]));

        settle.EnabledModules.Should().Contain("shell");
        settle.SettledToolIds.Should().NotContain("bash");
        settle.SettledToolIds.Should().Contain("powershell");
    }

    [Fact]
    public void Compute_CustomScenario_ViaCatalogResolve()
    {
        var catalog = CreateCatalog(
            processEnabled: ["git", "web"],
            modules:
            [
                Desc("git", ["git_status"]),
                Desc("web", ["web_fetch"]),
                Desc("memory", ["memory_search"]),
            ]);

        var session = SessionData.Create(scenario: "my-review");
        var settle = SessionSettlement.Compute(
            session,
            catalog,
            processScenario: "minimal",
            userToolsDisabled: null,
            resolveScenario: name => name == "my-review"
                ? new ScenarioDefinition(
                    "my-review",
                    ["git", "web"],
                    "general",
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    Array.Empty<string>())
                : BuiltInScenarios.TryGet(name));

        settle.ScenarioName.Should().Be("my-review");
        settle.EnabledModules.Should().BeEquivalentTo(["git", "web"]);
        settle.SettledToolIds.Should().Contain("git_status");
        settle.SettledToolIds.Should().Contain("web_fetch");
        settle.EnabledModules.Should().NotContain("memory");
    }

    [Fact]
    public void Compute_OverrideCannotWidenPastBoot()
    {
        var catalog = CreateCatalog(
            processEnabled: ["git", "filesystem"],
            modules:
            [
                Desc("git", ["git_status"]),
                Desc("filesystem", ["read"]),
                Desc("memory", ["memory_search"]) // available but not process-enabled
            ]);

        var session = SessionData.Create(scenario: "code");
        session.ScenarioOverride = new SessionScenarioOverride
        {
            Modules = new SessionModulesOverride
            {
                Enabled = ["git", "memory", "filesystem"]
            }
        };

        var settle = SessionSettlement.Compute(session, catalog, processScenario: "full", userToolsDisabled: null);

        settle.EnabledModules.Should().BeEquivalentTo(["filesystem", "git"]);
        settle.EnabledModules.Should().NotContain("memory");
        settle.SettledToolIds.Should().NotContain("memory_search");
    }

    [Fact]
    public void Compute_SessionToolsDisabled_AndUserToolsDisabled_AreSubtracted()
    {
        var catalog = CreateCatalog(
            processEnabled: ["git", "filesystem"],
            modules:
            [
                Desc("git", ["git_status", "git_commit"]),
                Desc("filesystem", ["read", "write"])
            ]);

        var session = SessionData.Create(scenario: "code");
        session.ScenarioOverride = new SessionScenarioOverride
        {
            Tools = new SessionToolsOverride { Disabled = ["git_commit"] }
        };

        var settle = SessionSettlement.Compute(
            session,
            catalog,
            processScenario: "full",
            userToolsDisabled: ["write"]);

        settle.SettledToolIds.Should().Contain("git_status", "read");
        settle.SettledToolIds.Should().NotContain("git_commit");
        settle.SettledToolIds.Should().NotContain("write");
    }

    [Fact]
    public void Compute_NullSessionScenario_FallsBackToProcessScenario()
    {
        var catalog = CreateCatalog(
            processEnabled: BuiltInScenarios.Work.Modules.ToArray(),
            modules: BuiltInScenarios.Work.Modules
                .Select(id => Desc(id, [$"{id}_tool"]))
                .ToArray());

        var session = SessionData.Create(); // Scenario null
        session.Scenario.Should().BeNull();

        var settle = SessionSettlement.Compute(session, catalog, processScenario: "work", userToolsDisabled: null);

        settle.ScenarioName.Should().Be("work");
        settle.EnabledModules.Should().Contain("memory");
        settle.EnabledModules.Should().NotContain("git");
    }

    [Fact]
    public void Compute_SnapshotIsIndependentOfLaterSessionMutation()
    {
        var catalog = CreateCatalog(
            processEnabled: ["git", "memory"],
            modules:
            [
                Desc("git", ["git_status"]),
                Desc("memory", ["memory_search"])
            ]);

        var session = SessionData.Create(scenario: "code");
        var snapshot = SessionSettlement.Compute(session, catalog, processScenario: "full", userToolsDisabled: null);

        session.Scenario = "work";
        snapshot.ScenarioName.Should().Be("code");
        snapshot.SettledToolIds.Should().Contain("git_status");
        snapshot.SettledToolIds.Should().NotContain("memory_search");
    }

    private static ModuleCatalog CreateCatalog(
        IReadOnlyList<string> processEnabled,
        IReadOnlyList<ModuleDescriptor> modules)
    {
        var catalog = new ModuleCatalog();
        catalog.ReplaceAvailable(modules);
        catalog.ReplaceEnabled(processEnabled);
        return catalog;
    }

    private static ModuleDescriptor Desc(string id, IReadOnlyList<string> tools) =>
        new(id, tools, Array.Empty<string>(), Array.Empty<string>());
}
