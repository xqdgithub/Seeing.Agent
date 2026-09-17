using FluentAssertions;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.WebUI.Models;
using Seeing.Agent.WebUI.Services;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

public class PermissionCardAggregatorTests
{
    private const string Session = "s1";

    private static PermissionRequest Pending(
        string requestId,
        string? callId,
        string kind = "tool.execute",
        IReadOnlyList<PermissionGrantScope>? scopes = null,
        string sessionId = Session,
        string? loopId = "loop-1")
        => new()
        {
            RequestId = requestId,
            SessionId = sessionId,
            CallId = callId,
            LoopId = loopId,
            PermissionKind = kind,
            Resource = "read",
            Message = "需要确认",
            AllowedScopes = scopes ?? new[] { PermissionGrantScope.Once, PermissionGrantScope.Session }
        };

    private static PermissionRequestEvent RequestEvent(PermissionRequest request)
        => new()
        {
            SessionId = request.SessionId,
            RequestId = request.RequestId!,
            CallId = request.CallId,
            LoopId = request.LoopId,
            PermissionKind = request.PermissionKind,
            Resource = request.Resource,
            Message = request.Message,
            RiskLevel = request.RiskLevel,
            AllowedScopes = request.AllowedScopes,
            Timestamp = DateTime.Now
        };

    private static PermissionResolvedEvent ResolvedEvent(
        string requestId,
        string sessionId = Session,
        PermissionEffect decision = PermissionEffect.Allow,
        PermissionGrantScope scope = PermissionGrantScope.Once,
        PermissionResolvedBy resolvedBy = PermissionResolvedBy.User,
        string? reason = null)
        => new()
        {
            SessionId = sessionId,
            RequestId = requestId,
            Decision = decision,
            Scope = scope,
            ResolvedBy = resolvedBy,
            Reason = reason
        };

    private static (PermissionCardAggregator Aggregator, FakePermissionRequestManager Manager) CreateBound(
        params PermissionRequest[] seeded)
    {
        var manager = new FakePermissionRequestManager();
        manager.Seed(seeded);
        var aggregator = new PermissionCardAggregator(manager);
        aggregator.Bind(Session);
        return (aggregator, manager);
    }

    [Fact]
    public void OnEvent_RequestWithCallId_ShouldCreatePendingCardLinkedToCallId()
    {
        var (aggregator, manager) = CreateBound();
        var request = Pending("r1", "call-1");
        manager.Seed(request);

        aggregator.OnEvent(RequestEvent(request));

        aggregator.GetByRequestId("r1").Should().NotBeNull();
        aggregator.GetByRequestId("r1")!.IsPending.Should().BeTrue();
        aggregator.GetByCallId("call-1").Should().ContainSingle(c => c.RequestId == "r1");
        aggregator.Snapshot.Should().ContainSingle();
    }

    [Fact]
    public void OnEvent_RequestWithoutCallId_ShouldStillCreateCard()
    {
        var (aggregator, manager) = CreateBound();
        var request = Pending("r1", callId: null);
        manager.Seed(request);

        aggregator.OnEvent(RequestEvent(request));

        aggregator.GetByRequestId("r1").Should().NotBeNull();
        aggregator.GetByRequestId("r1")!.CallId.Should().BeNull();
    }

    [Fact]
    public void OnEvent_RequestWithoutAllowedScopes_ShouldDefaultToOnceAndSession()
    {
        var (aggregator, manager) = CreateBound();
        var request = Pending("r1", "call-1");
        manager.Seed(request);
        var evt = RequestEvent(request) with { AllowedScopes = Array.Empty<PermissionGrantScope>() };

        aggregator.OnEvent(evt);

        var card = aggregator.GetByRequestId("r1")!;
        card.Allows(PermissionGrantScope.Once).Should().BeTrue();
        card.Allows(PermissionGrantScope.Session).Should().BeTrue();
        card.CanAllowSessionDirectory.Should().BeFalse();
    }

    [Fact]
    public void OnEvent_RepeatedRequestSameId_ShouldUpdateInPlaceWithoutDuplicating()
    {
        var (aggregator, manager) = CreateBound();
        var request = Pending("r1", "call-1");
        manager.Seed(request);

        aggregator.OnEvent(RequestEvent(request));
        aggregator.OnEvent(RequestEvent(request));

        aggregator.Snapshot.Should().ContainSingle();
    }

    [Fact]
    public void OnEvent_Resolved_ShouldConvergeCardToNonPendingWithDecision()
    {
        var (aggregator, manager) = CreateBound();
        var request = Pending("r1", "call-1");
        manager.Seed(request);
        aggregator.OnEvent(RequestEvent(request));

        aggregator.OnEvent(ResolvedEvent("r1", scope: PermissionGrantScope.Session));

        var card = aggregator.GetByRequestId("r1")!;
        card.IsPending.Should().BeFalse();
        card.Decision.Should().Be(PermissionEffect.Allow);
        card.Scope.Should().Be(PermissionGrantScope.Session);
        card.ResolvedBy.Should().Be(PermissionResolvedBy.User);
    }

    [Fact]
    public void OnEvent_ResolvedUnknownRequest_ShouldBeIgnored()
    {
        var (aggregator, _) = CreateBound();

        aggregator.OnEvent(ResolvedEvent("ghost"));

        aggregator.Snapshot.Should().BeEmpty();
    }

    [Fact]
    public void OnEvent_ResolvedTwice_ShouldConvergeOnFirstDecision()
    {
        var (aggregator, manager) = CreateBound();
        var request = Pending("r1", "call-1");
        manager.Seed(request);
        aggregator.OnEvent(RequestEvent(request));

        aggregator.OnEvent(ResolvedEvent("r1", decision: PermissionEffect.Allow));
        aggregator.OnEvent(ResolvedEvent("r1", decision: PermissionEffect.Deny));

        var card = aggregator.GetByRequestId("r1")!;
        card.IsPending.Should().BeFalse();
        card.Decision.Should().Be(PermissionEffect.Allow);
    }

    [Fact]
    public void OnEvent_StaleReplayAfterResolved_ShouldNotResurrectPending()
    {
        var (aggregator, manager) = CreateBound();
        var request = Pending("r1", "call-1");
        manager.Seed(request);
        aggregator.OnEvent(RequestEvent(request));
        aggregator.OnEvent(ResolvedEvent("r1"));
        manager.Remove("r1");

        // 缓冲重放：已决议请求的 PermissionRequestEvent 再次到达
        aggregator.OnEvent(RequestEvent(request));

        aggregator.GetByRequestId("r1")!.IsPending.Should().BeFalse();
    }

    [Fact]
    public void OnEvent_StaleReplayAfterResolved_ManagerStillPending_ShouldNotResurrectPending()
    {
        var (aggregator, manager) = CreateBound();
        var request = Pending("r1", "call-1");
        manager.Seed(request);
        aggregator.OnEvent(RequestEvent(request));
        aggregator.OnEvent(ResolvedEvent("r1"));

        // 即使 manager 尚未把请求移出在途，聚合器也不得复活已决议卡片
        aggregator.OnEvent(RequestEvent(request));

        aggregator.GetByRequestId("r1")!.IsPending.Should().BeFalse();
    }

    [Fact]
    public void OnEvent_RequestNotInGetPending_ShouldBeIgnored()
    {
        var (aggregator, _) = CreateBound();

        // 缓冲中残留的请求事件，但 manager 已不再视为在途
        aggregator.OnEvent(new PermissionRequestEvent
        {
            SessionId = Session,
            RequestId = "stale-1",
            CallId = "call-1",
            PermissionKind = "tool.execute",
            AllowedScopes = new[] { PermissionGrantScope.Once, PermissionGrantScope.Session }
        });

        aggregator.Snapshot.Should().BeEmpty();
    }

    [Fact]
    public void OnEvent_OtherSession_ShouldBeIgnored()
    {
        var (aggregator, manager) = CreateBound();
        var other = Pending("r2", "call-9", sessionId: "s2");
        manager.Seed(other);

        aggregator.OnEvent(RequestEvent(other));

        aggregator.Snapshot.Should().BeEmpty();
    }

    [Fact]
    public void Reconcile_ShouldRebuildPendingCardsFromManager()
    {
        var manager = new FakePermissionRequestManager();
        manager.Seed(Pending("r1", "call-1"), Pending("r2", "call-2"));
        var aggregator = new PermissionCardAggregator(manager);

        aggregator.Reconcile(Session);

        aggregator.Snapshot.Should().HaveCount(2);
        aggregator.Snapshot.Should().OnlyContain(c => c.IsPending);
        aggregator.GetByCallId("call-2").Should().ContainSingle(c => c.RequestId == "r2");
    }

    [Fact]
    public void Reconcile_DifferentSession_ShouldOnlyTakeThatSessionPending()
    {
        var manager = new FakePermissionRequestManager();
        manager.Seed(Pending("r1", "call-1"), Pending("r2", "call-2", sessionId: "s2"));
        var aggregator = new PermissionCardAggregator(manager);

        aggregator.Reconcile(Session);

        aggregator.Snapshot.Should().ContainSingle(c => c.RequestId == "r1");
    }

    [Fact]
    public void Bind_WhenSessionChanges_ShouldClearPreviousCards()
    {
        var manager = new FakePermissionRequestManager();
        manager.Seed(Pending("r1", "call-1"));
        var aggregator = new PermissionCardAggregator(manager);
        aggregator.Bind(Session);
        aggregator.Snapshot.Should().ContainSingle();

        aggregator.Bind("s2");

        aggregator.Snapshot.Should().BeEmpty();
    }

    [Fact]
    public void Changed_ShouldFireOnRequestAndResolve()
    {
        var (aggregator, manager) = CreateBound();
        var request = Pending("r1", "call-1");
        manager.Seed(request);
        var fired = 0;
        aggregator.Changed += () => fired++;

        aggregator.OnEvent(RequestEvent(request));
        aggregator.OnEvent(ResolvedEvent("r1"));

        fired.Should().Be(2);
    }

    [Fact]
    public void Card_NonFileSystem_ShouldNotOfferSessionDirectory()
    {
        var (aggregator, manager) = CreateBound();
        var request = Pending("r1", "call-1", kind: "tool.execute",
            scopes: new[] { PermissionGrantScope.Once, PermissionGrantScope.Session });
        manager.Seed(request);

        aggregator.OnEvent(RequestEvent(request));

        var card = aggregator.GetByRequestId("r1")!;
        card.IsFileSystem.Should().BeFalse();
        card.CanAllowSessionDirectory.Should().BeFalse();
    }

    [Fact]
    public void Card_FileSystem_WithDirectoryScope_ShouldOfferSessionDirectory()
    {
        var (aggregator, manager) = CreateBound();
        var request = Pending("r1", "call-1", kind: "filesystem.write",
            scopes: new[]
            {
                PermissionGrantScope.Once,
                PermissionGrantScope.Session,
                PermissionGrantScope.SessionDirectory
            });
        manager.Seed(request);

        aggregator.OnEvent(RequestEvent(request));

        var card = aggregator.GetByRequestId("r1")!;
        card.IsFileSystem.Should().BeTrue();
        card.CanAllowSessionDirectory.Should().BeTrue();
    }

    [Fact]
    public void Bind_EmptySessionId_ShouldBeIgnored()
    {
        var aggregator = new PermissionCardAggregator(new FakePermissionRequestManager());

        aggregator.Bind(string.Empty);

        aggregator.SessionId.Should().BeEmpty();
    }
}

/// <summary>测试替身：在途请求管理器（GetPending 可对账；TryResolve 仅记录调用）。</summary>
internal sealed class FakePermissionRequestManager : IPermissionRequestManager
{
    private readonly Dictionary<string, PermissionRequest> _pending = new(StringComparer.Ordinal);

    public List<ResolveCall> ResolveCalls { get; } = new();

    public bool ResolveResult { get; set; } = true;

    public void Seed(params PermissionRequest[] requests)
    {
        foreach (var request in requests)
        {
            if (!string.IsNullOrEmpty(request.RequestId))
                _pending[request.RequestId!] = request;
        }
    }

    public bool Remove(string requestId) => _pending.Remove(requestId);

    public Task<PermissionTicket> BeginAsync(PermissionRequest request, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<PermissionResolution> WaitAsync(PermissionTicket ticket, CancellationToken ct = default)
        => throw new NotSupportedException();

    public bool TryResolve(
        string requestId,
        PermissionEffect decision,
        PermissionGrantScope scope,
        PermissionResolvedBy resolvedBy,
        string? reason = null,
        string? expectedSessionId = null)
    {
        ResolveCalls.Add(new ResolveCall(requestId, decision, scope, resolvedBy, reason, expectedSessionId));
        return ResolveResult;
    }

    public IReadOnlyList<PermissionRequest> GetPending(string sessionId)
        => string.IsNullOrEmpty(sessionId)
            ? Array.Empty<PermissionRequest>()
            : _pending.Values
                .Where(r => string.Equals(r.SessionId, sessionId, StringComparison.Ordinal))
                .ToList();

    public int PendingCount => _pending.Count;

    public void Dispose()
    {
    }

    public sealed record ResolveCall(
        string RequestId,
        PermissionEffect Decision,
        PermissionGrantScope Scope,
        PermissionResolvedBy ResolvedBy,
        string? Reason,
        string? ExpectedSessionId);
}
