using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Core.Scenarios;
using Seeing.Agent.Hosting.Execution;
using Seeing.Agent.Core.Modules;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.App.Execution;

/// <summary>
/// P7-T9：会话级结算只收窄进程级 enabled；同进程多会话并行不同 scenario。
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

        // code 含 git，不含 memory；work 含 memory，不含 git
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
    public void Compute_SessionModulesOverride_CanOnlyNarrowNotAdd()
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
                // 试图「新增」进程未启用的 memory — 必须被 ∩ 掉
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

        session.Scenario = "work"; // 本轮之后切换；快照不变
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
