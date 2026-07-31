using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Permission;
using Xunit;

namespace Seeing.Agent.Tests.Permission;

public class PermissionServiceTests
{
    private readonly Mock<IPermissionCache> _cacheMock = new();
    private readonly Mock<IServiceProvider> _spMock = new();

    private PermissionService CreateService()
    {
        return new PermissionService(_spMock.Object, _cacheMock.Object, NullLogger<PermissionService>.Instance);
    }

    [Fact]
    public async Task EvaluateAsync_NoRules_ShouldReturnDefaultEffect()
    {
        var service = CreateService();
        var context = new PermissionContext
        {
            Policy = new AgentPermissionPolicy { DefaultEffect = PermissionEffect.Deny }
        };
        var resource = new ResourceIdentifier(PermissionKind.Tool, "read");

        var result = await service.EvaluateAsync(resource, context);

        result.Effect.Should().Be(PermissionEffect.Deny);
        result.IsDenied.Should().BeTrue();
    }

    [Fact]
    public async Task EvaluateAsync_AllowRule_ShouldReturnAllow()
    {
        var service = CreateService();
        var context = new PermissionContext
        {
            AgentName = "test-agent",
            Policy = new AgentPermissionPolicy
            {
                DefaultEffect = PermissionEffect.Deny,
                Rules = new[] { PermissionRuleEntry.Allow(PermissionKind.Tool, "read", 0) }
            }
        };
        var resource = new ResourceIdentifier(PermissionKind.Tool, "read");

        var result = await service.EvaluateAsync(resource, context);

        result.Effect.Should().Be(PermissionEffect.Allow);
        result.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task EvaluateAsync_DenyOverridesAllow_WhenHigherPriority()
    {
        var service = CreateService();
        var context = new PermissionContext
        {
            AgentName = "test-agent",
            Policy = new AgentPermissionPolicy
            {
                DefaultEffect = PermissionEffect.Deny,
                Rules = new PermissionRuleEntry[]
                {
                    PermissionRuleEntry.Allow(PermissionKind.Tool, "read", 10),
                    PermissionRuleEntry.Deny(PermissionKind.Tool, "read", 100),
                }
            }
        };
        var resource = new ResourceIdentifier(PermissionKind.Tool, "read");

        var result = await service.EvaluateAsync(resource, context);

        result.Effect.Should().Be(PermissionEffect.Deny);
    }

    [Fact]
    public async Task EvaluateAsync_WildcardTool_ShouldMatchAllTools()
    {
        var service = CreateService();
        var context = new PermissionContext
        {
            AgentName = "test-agent",
            Policy = new AgentPermissionPolicy
            {
                DefaultEffect = PermissionEffect.Deny,
                Rules = new[] { PermissionRuleEntry.Allow(PermissionKind.Tool, "*", 0) }
            }
        };
        var resource = new ResourceIdentifier(PermissionKind.Tool, "bash");

        var result = await service.EvaluateAsync(resource, context);

        result.Effect.Should().Be(PermissionEffect.Allow);
    }

    [Fact]
    public async Task EvaluateToolAsync_ShouldDelegateToEvaluateAsync()
    {
        var service = CreateService();
        var context = new PermissionContext
        {
            AgentName = "test-agent",
            Policy = new AgentPermissionPolicy
            {
                Rules = new[] { PermissionRuleEntry.Allow(PermissionKind.Tool, "bash", 0) }
            }
        };

        var result = await service.EvaluateToolAsync("bash", null, context);

        result.Effect.Should().Be(PermissionEffect.Allow);
    }
}
