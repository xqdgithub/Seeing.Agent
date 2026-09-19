using FluentAssertions;
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

    private static (PermissionInbox Inbox, FakePermissionRequestManager Manager, PermissionInteractionService Service)
        Setup(PermissionRequest request)
    {
        var manager = new FakePermissionRequestManager();
        manager.Seed(request);
        var inbox = new PermissionInbox(manager);
        var service = new PermissionInteractionService(manager);
        return (inbox, manager, service);
    }

    [Fact]
    public void AllowOnce_ShouldResolveWithOnceScopeAndUser()
    {
        var request = Request();
        var (inbox, manager, service) = Setup(request);

        var result = service.AllowOnce(inbox.GetBySession(Session).Single());

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
        var (inbox, manager, service) = Setup(request);

        var result = service.AllowSession(inbox.GetBySession(Session).Single());

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
        var (inbox, manager, service) = Setup(request);

        var result = service.AllowSessionDirectory(inbox.GetBySession(Session).Single());

        result.Should().BeTrue();
        manager.ResolveCalls.Should().ContainSingle();
        manager.ResolveCalls[0].Decision.Should().Be(PermissionEffect.Allow);
        manager.ResolveCalls[0].Scope.Should().Be(PermissionGrantScope.SessionDirectory);
    }

    [Fact]
    public void AllowSessionDirectory_WhenScopeNotAllowed_ShouldNoOp()
    {
        var request = Request();
        var (inbox, manager, service) = Setup(request);

        var result = service.AllowSessionDirectory(inbox.GetBySession(Session).Single());

        result.Should().BeFalse();
        manager.ResolveCalls.Should().BeEmpty();
    }

    [Fact]
    public void DenyOnce_ShouldResolveWithDenyAndOnce()
    {
        var request = Request();
        var (inbox, manager, service) = Setup(request);

        var result = service.DenyOnce(inbox.GetBySession(Session).Single());

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
        var (inbox, manager, service) = Setup(request);

        var result = service.DenySession(inbox.GetBySession(Session).Single());

        result.Should().BeTrue();
        manager.ResolveCalls.Should().ContainSingle();
        manager.ResolveCalls[0].Decision.Should().Be(PermissionEffect.Deny);
        manager.ResolveCalls[0].Scope.Should().Be(PermissionGrantScope.Session);
    }

    [Fact]
    public void Action_WhenCardAlreadyResolved_ShouldNoOp()
    {
        var manager = new FakePermissionRequestManager();
        var service = new PermissionInteractionService(manager);
        var resolvedCard = new PermissionCardModel
        {
            RequestId = "r1",
            SessionId = Session,
            PermissionKind = "tool.execute",
            AllowedScopes = new[] { PermissionGrantScope.Once, PermissionGrantScope.Session },
            IsPending = false
        };

        var result = service.AllowOnce(resolvedCard);

        result.Should().BeFalse();
        manager.ResolveCalls.Should().BeEmpty();
    }

    [Fact]
    public void Action_WhenManagerRejects_ShouldReturnFalse()
    {
        var request = Request();
        var (inbox, manager, service) = Setup(request);
        manager.ResolveResult = false;

        var result = service.AllowOnce(inbox.GetBySession(Session).Single());

        result.Should().BeFalse();
        manager.ResolveCalls.Should().ContainSingle();
    }

    [Fact]
    public void PendingCard_ShouldExposeAllActionScopes()
    {
        var request = Request("filesystem.write", new[]
        {
            PermissionGrantScope.Once,
            PermissionGrantScope.Session,
            PermissionGrantScope.SessionDirectory
        });
        var (inbox, _, _) = Setup(request);

        var card = inbox.GetBySession(Session).Single();

        card.CanAllowOnce.Should().BeTrue();
        card.CanAllowSession.Should().BeTrue();
        card.CanAllowSessionDirectory.Should().BeTrue();
        card.CanDenyOnce.Should().BeTrue();
        card.CanDenySession.Should().BeTrue();
    }
}
