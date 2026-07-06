using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Tests.Gateway;

public class AgentSelectionResolverTests
{
    private readonly Mock<IAgentRegistry> _registryMock = new();

    [Fact]
    public async Task ResolveAgentIdAsync_RequestAgentId_ShouldTakePriority()
    {
        var resolver = CreateResolver(new SeeingAgentOptions());

        var result = await resolver.ResolveAgentIdAsync("custom", "session-agent");

        result.Should().Be("custom");
    }

    [Fact]
    public async Task ResolveAgentIdAsync_ShouldUseSessionSelectedAgentBeforeRegistry()
    {
        var resolver = CreateResolver(new SeeingAgentOptions { DefaultAgent = "build" });

        var result = await resolver.ResolveAgentIdAsync(null, "session-agent");

        result.Should().Be("session-agent");
    }

    [Fact]
    public async Task ResolveAgentIdAsync_ShouldFallBackToRegistryDefault()
    {
        _registryMock
            .Setup(r => r.GetDefaultAgentNameAsync())
            .ReturnsAsync("acp-opencode");

        var resolver = CreateResolver(new SeeingAgentOptions());

        var result = await resolver.ResolveAgentIdAsync(null, null);

        result.Should().Be("acp-opencode");
    }

    [Fact]
    public void ResolveModelId_ShouldFollowGlobalAndAgentFallbackChain()
    {
        var resolver = CreateResolver(new SeeingAgentOptions
        {
            DefaultModel = "global/model",
            Agents = new Dictionary<string, AgentConfig>
            {
                ["sisyphus"] = new() { Model = "agent/model" }
            }
        });

        resolver.ResolveModelId(null, null, "sisyphus").Should().Be("global/model");
        resolver.ResolveModelId(null, "session/model", "sisyphus").Should().Be("session/model");
        resolver.ResolveModelId("request/model", null, "sisyphus").Should().Be("request/model");

        var resolverAgentOnly = CreateResolver(new SeeingAgentOptions
        {
            Agents = new Dictionary<string, AgentConfig>
            {
                ["sisyphus"] = new() { Model = "agent/model" }
            }
        });

        resolverAgentOnly.ResolveModelId(null, null, "sisyphus").Should().Be("agent/model");
    }

    [Fact]
    public void ResolveAcpModelId_ShouldNotFallBackToDefaultModel()
    {
        var resolver = CreateResolver(new SeeingAgentOptions
        {
            DefaultModel = "anthropic/GLM-5",
            Agents = new Dictionary<string, AgentConfig>
            {
                ["acp-opencode"] = new() { Model = "agent/model" }
            }
        });

        resolver.ResolveAcpModelId(null, null).Should().BeNull();
        resolver.ResolveAcpModelId(null, "session/model").Should().Be("session/model");
        resolver.ResolveAcpModelId("seeing-coding-plan/GLM-5", null).Should().Be("seeing-coding-plan/GLM-5");
    }

    [Fact]
    public void ResolveAcpModeId_ShouldFollowRequestThenSession()
    {
        var resolver = CreateResolver(new SeeingAgentOptions());

        resolver.ResolveAcpModeId("build", "ask").Should().Be("build");
        resolver.ResolveAcpModeId(null, "ask").Should().Be("ask");
        resolver.ResolveAcpModeId(null, null).Should().BeNull();
    }

    [Fact]
    public void MergeGatewayOptions_ProjectLevel_ShouldOverridePermissionButKeepUserPort()
    {
        var user = new GatewayOptions
        {
            Enabled = true,
            Port = 9000,
            PermissionMode = "interactive"
        };

        var project = new GatewayOptions
        {
            PermissionMode = "auto_approve",
            Port = 0
        };

        var merged = MergeDeep.Merge(user, project);

        merged.Port.Should().Be(9000);
        merged.PermissionMode.Should().Be("auto_approve");
    }

    private AgentSelectionResolver CreateResolver(SeeingAgentOptions seeingOptions) =>
        new(Options.Create(seeingOptions), _registryMock.Object);
}

public class LegacyGatewayConfigMigratorTests
{
    [Fact]
    public void Apply_ShouldMigrateDefaultAgentIdToRootDefaultAgent()
    {
        using var doc = JsonDocument.Parse("""{"DefaultAgentId":"sisyphus"}""");
        var options = new SeeingAgentOptions();

        LegacyGatewayConfigMigrator.Apply(doc.RootElement, options);

        options.DefaultAgent.Should().Be("sisyphus");
    }

    [Fact]
    public void Apply_ShouldMigrateDefaultAcpBackendToAcpAgentName()
    {
        using var doc = JsonDocument.Parse("""{"DefaultAcpBackend":"opencode"}""");
        var options = new SeeingAgentOptions();

        LegacyGatewayConfigMigrator.Apply(doc.RootElement, options);

        options.DefaultAgent.Should().Be("acp-opencode");
    }

    [Fact]
    public void Apply_ShouldNotOverrideExistingDefaultAgent()
    {
        using var doc = JsonDocument.Parse("""{"DefaultAgentId":"legacy"}""");
        var options = new SeeingAgentOptions { DefaultAgent = "current" };

        LegacyGatewayConfigMigrator.Apply(doc.RootElement, options);

        options.DefaultAgent.Should().Be("current");
    }
}
