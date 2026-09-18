using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Session.Core;
using Seeing.Session.Management;
using Xunit;

namespace Seeing.Session.Tests;

/// <summary>
/// SessionData.Scenario / SessionScenarioOverride 与存量兼容。
/// </summary>
public class SessionScenarioTests
{
    private static readonly JsonSerializerOptions s_storeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public void DeserializeLegacyJson_WithoutScenario_ShouldBeNull()
    {
        var json = """{"id":"ses_legacy","title":"Old","partitionId":"default","messages":[]}""";

        var data = JsonSerializer.Deserialize<SessionData>(json, s_storeOptions);

        data.Should().NotBeNull();
        data!.Scenario.Should().BeNull();
        data.ScenarioOverride.Should().BeNull();
    }

    [Fact]
    public void ResolveScenario_WhenSessionScenarioNull_FallsBackToProcessLevel()
    {
        var data = SessionData.Create();

        data.Scenario.Should().BeNull();
        data.ResolveScenario("code").Should().Be("code");
        data.ResolveScenario(null).Should().BeNull();
    }

    [Fact]
    public void ResolveScenario_WhenSessionScenarioSet_PrefersSession()
    {
        var data = SessionData.Create(scenario: "work");

        data.ResolveScenario("code").Should().Be("work");
    }

    [Fact]
    public void Create_WithScenario_ShouldPersistOnSessionData()
    {
        var mgr = new SessionManager(logger: new NullLogger<SessionManager>());

        var session = mgr.Create(partitionId: "p1", selectedAgent: "build", scenario: "code");

        session.Scenario.Should().Be("code");
    }

    [Fact]
    public void Create_WithNullScenario_MeansProcessLevelFallback()
    {
        var session = SessionData.Create(scenario: null);

        session.Scenario.Should().BeNull();
        session.ResolveScenario("minimal").Should().Be("minimal");
    }

    [Fact]
    public void SerializeRoundTrip_ShouldPreserveScenarioAndOverride()
    {
        var data = SessionData.Create(scenario: "research");
        data.ScenarioOverride = new SessionScenarioOverride
        {
            Modules = new SessionModulesOverride { Enabled = ["web", "memory"] },
            Tools = new SessionToolsOverride { Disabled = ["shell"] }
        };

        var json = JsonSerializer.Serialize(data, s_storeOptions);
        var round = JsonSerializer.Deserialize<SessionData>(json, s_storeOptions);

        round!.Scenario.Should().Be("research");
        round.ScenarioOverride.Should().NotBeNull();
        round.ScenarioOverride!.Modules!.Enabled.Should().Equal("web", "memory");
        round.ScenarioOverride.Tools!.Disabled.Should().Equal("shell");
    }

    [Fact]
    public void Clone_ShouldCopyScenarioAndOverride()
    {
        var data = SessionData.Create(scenario: "full");
        data.ScenarioOverride = new SessionScenarioOverride
        {
            Tools = new SessionToolsOverride { Disabled = ["git_commit"] }
        };

        var clone = data.Clone();

        clone.Scenario.Should().Be("full");
        clone.ScenarioOverride.Should().NotBeNull();
        clone.ScenarioOverride!.Tools!.Disabled.Should().Equal("git_commit");
        clone.ScenarioOverride.Should().NotBeSameAs(data.ScenarioOverride);
    }
}
