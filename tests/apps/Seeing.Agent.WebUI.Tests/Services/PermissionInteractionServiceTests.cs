using FluentAssertions;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.WebUI.Models;
using Seeing.Agent.WebUI.Services;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

public class PermissionInteractionServiceTests
{
    private const string Session = "s1";

    private static PermissionRequest Request(
        string kind = "tool.execute",
        IReadOnlyList<PermissionGrantScope>? scopes = null)
        => new()
        {
            RequestId = "r1",
            SessionId = Session,
            CallId = "call-1",
            LoopId = "loop-1",
            PermissionKind = kind,
            Resource = "read",
            AllowedScopes = scopes ?? new[] { PermissionGrantScope.Once, PermissionGrantScope.Session }
        };

    private static (PermissionCardAggregator Aggregator, FakePermissionRequestManager Manager, PermissionInteractionService Service)
        Setup(PermissionRequest request)
    {
        var manager = new FakePermissionRequestManager();
        manager.Seed(request);
        var aggregator = new PermissionCardAggregator(manager);
        aggregator.Bind(Session);
        var service = new PermissionInteractionService(manager);
        return (aggregator, manager, service);
    }

    private static PermissionCardModel PendingCard(PermissionRequest request)
        => Setup(request).Aggregator.GetByRequestId(request.RequestId!)!;

    [Fact]
    public void AllowOnce_ShouldResolveWithOnceScopeAndUser()
    {
        var request = Request();
        var (aggregator, manager, service) = Setup(request);

        var result = service.AllowOnce(aggregator.GetByRequestId("r1")!);

        result.Should().BeTrue();
        manager.ResolveCalls.Should().ContainSingle();
        var call = manager.ResolveCalls[0];
        call.RequestId.Should().Be("r1");
        call.Decision.Should().Be(PermissionEffect.Allow);
        call.Scope.Should().Be(PermissionGrantScope.Once);
        call.ResolvedBy.Should().Be(PermissionResolvedBy.User);
        call.ExpectedSessionId.Should().Be(Session);
    }

    [Fact]
    public void AllowSession_ShouldResolveWithSessionScope()
    {
        var request = Request();
        var (aggregator, manager, service) = Setup(request);

        var result = service.AllowSession(aggregator.GetByRequestId("r1")!);

        result.Should().BeTrue();
        manager.ResolveCalls.Should().ContainSingle();
        manager.ResolveCalls[0].Decision.Should().Be(PermissionEffect.Allow);
        manager.ResolveCalls[0].Scope.Should().Be(PermissionGrantScope.Session);
    }

    [Fact]
    public void AllowSessionDirectory_FileSystem_ShouldResolveWithDirectoryScope()
    {
        var request = Request("filesystem.write", new[]
        {
            PermissionGrantScope.Once,
            PermissionGrantScope.Session,
            PermissionGrantScope.SessionDirectory
        });
        var (aggregator, manager, service) = Setup(request);

        var result = service.AllowSessionDirectory(aggregator.GetByRequestId("r1")!);

        result.Should().BeTrue();
        manager.ResolveCalls.Should().ContainSingle();
        manager.ResolveCalls[0].Decision.Should().Be(PermissionEffect.Allow);
        manager.ResolveCalls[0].Scope.Should().Be(PermissionGrantScope.SessionDirectory);
    }

    [Fact]
    public void AllowSessionDirectory_WhenScopeNotAllowed_ShouldNoOp()
    {
        var request = Request();
        var (aggregator, manager, service) = Setup(request);

        var result = service.AllowSessionDirectory(aggregator.GetByRequestId("r1")!);

        result.Should().BeFalse();
        manager.ResolveCalls.Should().BeEmpty();
    }

    [Fact]
    public void DenyOnce_ShouldResolveWithDenyAndOnce()
    {
        var request = Request();
        var (aggregator, manager, service) = Setup(request);

        var result = service.DenyOnce(aggregator.GetByRequestId("r1")!);

        result.Should().BeTrue();
        manager.ResolveCalls.Should().ContainSingle();
        manager.ResolveCalls[0].Decision.Should().Be(PermissionEffect.Deny);
        manager.ResolveCalls[0].Scope.Should().Be(PermissionGrantScope.Once);
        manager.ResolveCalls[0].ResolvedBy.Should().Be(PermissionResolvedBy.User);
    }

    [Fact]
    public void DenySession_ShouldResolveWithDenyAndSession()
    {
        var request = Request();
        var (aggregator, manager, service) = Setup(request);

        var result = service.DenySession(aggregator.GetByRequestId("r1")!);

        result.Should().BeTrue();
        manager.ResolveCalls.Should().ContainSingle();
        manager.ResolveCalls[0].Decision.Should().Be(PermissionEffect.Deny);
        manager.ResolveCalls[0].Scope.Should().Be(PermissionGrantScope.Session);
    }

    [Fact]
    public void Action_WhenCardAlreadyResolved_ShouldNoOp()
    {
        var request = Request();
        var manager = new FakePermissionRequestManager();
        manager.Seed(request);
        var aggregator = new PermissionCardAggregator(manager);
        aggregator.Bind(Session);
        aggregator.OnEvent(new PermissionResolvedEvent
        {
            SessionId = Session,
            RequestId = "r1",
            Decision = PermissionEffect.Allow,
            Scope = PermissionGrantScope.Once,
            ResolvedBy = PermissionResolvedBy.User
        });
        var service = new PermissionInteractionService(manager);
        var card = aggregator.GetByRequestId("r1")!;

        var result = service.AllowOnce(card);

        result.Should().BeFalse();
        manager.ResolveCalls.Should().BeEmpty();
    }

    [Fact]
    public void Action_WhenManagerRejects_ShouldReturnFalse()
    {
        var request = Request();
        var (aggregator, manager, service) = Setup(request);
        manager.ResolveResult = false;

        var result = service.AllowOnce(aggregator.GetByRequestId("r1")!);

        result.Should().BeFalse();
        manager.ResolveCalls.Should().ContainSingle();
    }

    [Fact]
    public void PendingCard_ShouldExposeAllActionScopes()
    {
        var card = PendingCard(Request("filesystem.write", new[]
        {
            PermissionGrantScope.Once,
            PermissionGrantScope.Session,
            PermissionGrantScope.SessionDirectory
        }));

        card.CanAllowOnce.Should().BeTrue();
        card.CanAllowSession.Should().BeTrue();
        card.CanAllowSessionDirectory.Should().BeTrue();
        card.CanDenyOnce.Should().BeTrue();
        card.CanDenySession.Should().BeTrue();
    }
}
