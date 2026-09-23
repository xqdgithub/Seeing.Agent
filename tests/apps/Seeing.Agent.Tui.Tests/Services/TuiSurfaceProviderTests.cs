using System.Runtime.CompilerServices;
using FluentAssertions;
using Moq;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Questions;
using Seeing.Agent.Tui.Services;
using Seeing.Session.Core;

namespace Seeing.Agent.Tui.Tests.Services;

public sealed class TuiSurfaceProviderTests
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan EmptyConfirm = TimeSpan.FromMilliseconds(200);

    [Fact]
    public async Task SetActiveSessionAsync_ShouldIncludeGroupMembersAndRecursiveChildren()
    {
        var groups = new Mock<ISessionGroupManager>();
        var bus = new Mock<ISessionGroupEventBus>();
        var permissions = new Mock<IPermissionRequestManager>();
        var questions = new Mock<IQuestionRequestManager>();

        using var provider = CreateProvider(groups, bus, permissions, questions);

        groups.Setup(g => g.GetGroupForSessionAsync("root", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionGroup
            {
                Id = "g1",
                AnchorSessionId = "root",
                Members =
                [
                    new SessionGroupMember { SessionId = "root", IsAnchor = true },
                    new SessionGroupMember { SessionId = "fork1", Relation = SessionRelation.Fork },
                ],
            });
        groups.Setup(g => g.ListChildrenAsync("root", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new SessionData { Id = "c1" }]);
        groups.Setup(g => g.ListChildrenAsync("c1", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new SessionData { Id = "c2" }]);

        provider.Initialize();
        await provider.SetActiveSessionAsync("root", TestContext.Current.CancellationToken);

        provider.SurfaceSessionIds.Should().Contain(["root", "c1", "c2", "fork1"]);
    }

    [Fact]
    public async Task SetActiveSessionAsync_ShouldReplacePreviousSnapshot()
    {
        var groups = new Mock<ISessionGroupManager>();
        var bus = new Mock<ISessionGroupEventBus>();
        var permissions = new Mock<IPermissionRequestManager>();
        var questions = new Mock<IQuestionRequestManager>();

        using var provider = CreateProvider(groups, bus, permissions, questions, Debounce, EmptyConfirm);
        provider.Initialize();

        await provider.SetActiveSessionAsync("a", TestContext.Current.CancellationToken);
        provider.SurfaceSessionIds.Should().ContainSingle().Which.Should().Be("a");

        await provider.SetActiveSessionAsync("b", TestContext.Current.CancellationToken);
        await Task.Delay(250, TestContext.Current.CancellationToken);

        provider.SurfaceSessionIds.Should().ContainSingle().Which.Should().Be("b");
    }

    [Fact]
    public async Task PendingRequestSession_ShouldBeMergedAfterDebounce()
    {
        var groups = new Mock<ISessionGroupManager>();
        var bus = new Mock<ISessionGroupEventBus>();
        var permissions = new Mock<IPermissionRequestManager>();
        var questions = new Mock<IQuestionRequestManager>();
        var pending = new List<PermissionRequest>();

        using var provider = CreateProvider(groups, bus, permissions, questions, Debounce, EmptyConfirm);
        permissions.Setup(p => p.GetAllPending()).Returns(() => pending.ToList());

        provider.Initialize();
        await provider.SetActiveSessionAsync("s1", TestContext.Current.CancellationToken);

        pending.Add(new PermissionRequest { SessionId = "other", PermissionKind = "tool.execute" });
        permissions.Raise(p => p.PendingChanged += null);

        provider.SurfaceSessionIds.Should().NotContain("other");
        await Task.Delay(250, TestContext.Current.CancellationToken);
        provider.SurfaceSessionIds.Should().Contain("other");
    }

    [Fact]
    public async Task Debounce_ShouldCollapseRapidChangesIntoOneNotification()
    {
        var groups = new Mock<ISessionGroupManager>();
        var bus = new Mock<ISessionGroupEventBus>();
        var permissions = new Mock<IPermissionRequestManager>();
        var questions = new Mock<IQuestionRequestManager>();
        var pending = new List<PermissionRequest>();

        using var provider = CreateProvider(groups, bus, permissions, questions, Debounce, EmptyConfirm);
        permissions.Setup(p => p.GetAllPending()).Returns(() => pending.ToList());

        provider.Initialize();
        await provider.SetActiveSessionAsync("s1", TestContext.Current.CancellationToken);

        var count = 0;
        provider.SurfacedChanged += () => Interlocked.Increment(ref count);

        pending.Add(new PermissionRequest { SessionId = "o1", PermissionKind = "tool.execute" });
        permissions.Raise(p => p.PendingChanged += null);
        pending.Add(new PermissionRequest { SessionId = "o2", PermissionKind = "tool.execute" });
        permissions.Raise(p => p.PendingChanged += null);
        pending.Add(new PermissionRequest { SessionId = "o3", PermissionKind = "tool.execute" });
        permissions.Raise(p => p.PendingChanged += null);

        await Task.Delay(300, TestContext.Current.CancellationToken);

        count.Should().Be(1);
        provider.SurfaceSessionIds.Should().Contain(["o1", "o2", "o3"]);
    }

    [Fact]
    public async Task EmptyCandidate_ShouldBeDeferredUntilConfirmWindowElapses()
    {
        var groups = new Mock<ISessionGroupManager>();
        var bus = new Mock<ISessionGroupEventBus>();
        var permissions = new Mock<IPermissionRequestManager>();
        var questions = new Mock<IQuestionRequestManager>();

        using var provider = CreateProvider(
            groups, bus, permissions, questions,
            TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(250));

        provider.Initialize();
        await provider.SetActiveSessionAsync("s1", TestContext.Current.CancellationToken);
        provider.SurfaceSessionIds.Should().Contain("s1");

        await provider.SetActiveSessionAsync(string.Empty, TestContext.Current.CancellationToken);

        await Task.Delay(150, TestContext.Current.CancellationToken);
        provider.SurfaceSessionIds.Should().Contain("s1");

        await Task.Delay(400, TestContext.Current.CancellationToken);
        provider.SurfaceSessionIds.Should().BeEmpty();
    }

    [Fact]
    public async Task EmptyConfirm_ShouldBeCancelledWhenSnapshotRecovers()
    {
        var groups = new Mock<ISessionGroupManager>();
        var bus = new Mock<ISessionGroupEventBus>();
        var permissions = new Mock<IPermissionRequestManager>();
        var questions = new Mock<IQuestionRequestManager>();

        using var provider = CreateProvider(
            groups, bus, permissions, questions,
            TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(250));

        provider.Initialize();
        await provider.SetActiveSessionAsync("s1", TestContext.Current.CancellationToken);

        await provider.SetActiveSessionAsync(string.Empty, TestContext.Current.CancellationToken);
        await Task.Delay(80, TestContext.Current.CancellationToken);
        await provider.SetActiveSessionAsync("s2", TestContext.Current.CancellationToken);

        await Task.Delay(150, TestContext.Current.CancellationToken);
        provider.SurfaceSessionIds.Should().Contain("s2");
    }

    [Fact]
    public async Task Dispose_ShouldStopNotifications()
    {
        var groups = new Mock<ISessionGroupManager>();
        var bus = new Mock<ISessionGroupEventBus>();
        var permissions = new Mock<IPermissionRequestManager>();
        var questions = new Mock<IQuestionRequestManager>();
        var pending = new List<PermissionRequest>();

        var provider = CreateProvider(groups, bus, permissions, questions, Debounce, EmptyConfirm);
        permissions.Setup(p => p.GetAllPending()).Returns(() => pending.ToList());

        provider.Initialize();
        await provider.SetActiveSessionAsync("s1", TestContext.Current.CancellationToken);

        var count = 0;
        provider.SurfacedChanged += () => Interlocked.Increment(ref count);

        provider.Dispose();

        pending.Add(new PermissionRequest { SessionId = "other", PermissionKind = "tool.execute" });
        permissions.Raise(p => p.PendingChanged += null);

        await Task.Delay(300, TestContext.Current.CancellationToken);

        count.Should().Be(0);
        provider.SurfaceSessionIds.Should().BeEmpty();
    }

    private static TuiSurfaceProvider CreateProvider(
        Mock<ISessionGroupManager> groups,
        Mock<ISessionGroupEventBus> bus,
        Mock<IPermissionRequestManager> permissions,
        Mock<IQuestionRequestManager> questions,
        TimeSpan? debounce = null,
        TimeSpan? emptyConfirm = null)
    {
        bus.Setup(b => b.SubscribeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(EmptyEvents);
        permissions.Setup(p => p.GetAllPending()).Returns(Array.Empty<PermissionRequest>());
        questions.Setup(q => q.GetAllPending()).Returns(Array.Empty<QuestionRequest>());
        groups.Setup(g => g.GetGroupForSessionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SessionGroup?)null);
        groups.Setup(g => g.ListChildrenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SessionData>());

        return new TuiSurfaceProvider(
            groups.Object,
            bus.Object,
            permissions.Object,
            questions.Object,
            debounce ?? Debounce,
            emptyConfirm ?? EmptyConfirm);
    }

    private static IAsyncEnumerable<SessionGroupChangedEvent> EmptyEvents() => EnumerateEmpty();

    private static async IAsyncEnumerable<SessionGroupChangedEvent> EnumerateEmpty(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}
