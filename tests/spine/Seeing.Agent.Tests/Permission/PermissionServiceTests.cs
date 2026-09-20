using Seeing.Agent.Abstractions.Interactions;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Core.Interactions;
using Seeing.Agent.Core.Permission;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Seeing.Agent.Tests.Permission;

public class PermissionServiceTests
{
    private static PermissionService CreateService() => new(NullLogger<PermissionService>.Instance);

    [Fact]
    public async Task EvaluateToolAsync_AllowRule_ShouldReturnAllow()
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
        result.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task EvaluateToolAsync_NoMatchingRule_ShouldReturnDefaultEffect()
    {
        var service = CreateService();
        var context = new PermissionContext
        {
            Policy = new AgentPermissionPolicy { DefaultEffect = PermissionEffect.Deny }
        };

        var result = await service.EvaluateToolAsync("read", null, context);

        result.Effect.Should().Be(PermissionEffect.Deny);
        result.IsDenied.Should().BeTrue();
    }

    [Fact]
    public async Task EvaluateToolAsync_WildcardRule_ShouldMatchAllTools()
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

        var result = await service.EvaluateToolAsync("bash", null, context);

        result.Effect.Should().Be(PermissionEffect.Allow);
    }

    [Fact]
    public async Task EvaluateSkillAsync_AllowRule_ShouldReturnAllow()
    {
        var service = CreateService();
        var context = new PermissionContext
        {
            AgentName = "test-agent",
            Policy = new AgentPermissionPolicy
            {
                DefaultEffect = PermissionEffect.Deny,
                Rules = new[] { PermissionRuleEntry.Allow(PermissionKind.Skill, "my-skill", 0) }
            }
        };

        var result = await service.EvaluateSkillAsync("my-skill", context);

        result.Effect.Should().Be(PermissionEffect.Allow);
    }

    [Fact]
    public async Task AuthorizeAsync_WithoutPresenter_ShouldDenyNoChannel()
    {
        var service = CreateService();
        var request = new PermissionRequest
        {
            SessionId = "s1",
            PermissionKind = "tool.execute",
            Resource = "bash"
        };

        var resolution = await service.AuthorizeAsync(request);

        resolution.Decision.Should().Be(PermissionEffect.Deny);
        resolution.ResolvedBy.Should().Be(PermissionResolvedBy.NoChannel);
        resolution.SessionId.Should().Be("s1");
        resolution.RequestId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task AuthorizeAsync_WithPresenterNotSurfacingSession_ShouldDenyNoChannel()
    {
        var presentation = new SurfaceRegistry();
        presentation.Register(new FixedPresenter("other-session"));
        var manager = new Mock<IPermissionRequestManager>();
        var service = new PermissionService(
            NullLogger<PermissionService>.Instance,
            requestManager: manager.Object,
            presentation: presentation);

        var resolution = await service.AuthorizeAsync(new PermissionRequest
        {
            SessionId = "s1",
            PermissionKind = "tool.execute",
            Resource = "bash"
        });

        resolution.Decision.Should().Be(PermissionEffect.Deny);
        resolution.ResolvedBy.Should().Be(PermissionResolvedBy.NoChannel);
        manager.Verify(
            m => m.BeginAsync(It.IsAny<PermissionRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private sealed class FixedPresenter : ISurfaceProvider
    {
        private readonly IReadOnlyCollection<string> _surface;

        public FixedPresenter(params string[] sessionIds) => _surface = sessionIds;

        public IReadOnlyCollection<string> SurfaceSessionIds => _surface;

        public event Action? SurfacedChanged;

        public void Raise() => SurfacedChanged?.Invoke();
    }
}
