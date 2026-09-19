using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Core.Execution;
using Seeing.Agent.WebUI.Services;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

public class WebUiPermissionPresenterTests
{
    private static SessionGroup AnchorAndChildGroup() => new()
    {
        Id = "g1",
        AnchorSessionId = "anchor",
        ActiveSessionId = "anchor",
        Version = 1,
        Members = new List<SessionGroupMember>
        {
            new() { SessionId = "anchor", Relation = SessionRelation.None, IsAnchor = true, Order = 0 },
            new() { SessionId = "child", Relation = SessionRelation.Child, ParentSessionId = "anchor", Order = 1 }
        }
    };

    private static SessionWindowRegistry CreateRegistry(SessionGroup group)
    {
        var gm = new Mock<ISessionGroupManager>();
        gm.Setup(m => m.GetGroupForSessionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(group);
        gm.Setup(m => m.GetGroupAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(group);
        return new SessionWindowRegistry(
            gm.Object, new ChannelSessionGroupEventBus(), Mock.Of<ISessionManager>(),
            NullLogger<SessionWindowRegistry>.Instance);
    }

    private static async Task<SessionWindowRegistry> ReadyRegistryAsync()
    {
        var registry = CreateRegistry(AnchorAndChildGroup());
        registry.Rebind("anchor");
        await Task.Delay(200);
        return registry;
    }

    [Fact]
    public async Task Surface_ShouldIncludeWindowsAndAnchor()
    {
        var registry = await ReadyRegistryAsync();
        var store = new FakePermissionPresentationStore();

        using var presenter = new WebUiPermissionPresenter(
            store, () => registry,
            TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(200),
            NullLogger<WebUiPermissionPresenter>.Instance);

        presenter.SurfaceSessionIds.Should().BeEquivalentTo(new[] { "anchor", "child" });
        store.RegisterCount.Should().Be(1);
        store.CanSurface("child").Should().BeTrue();
    }

    [Fact]
    public async Task TransientEmptyWithinConfirmWindow_ShouldNotReportEmpty()
    {
        var registry = await ReadyRegistryAsync();
        var store = new FakePermissionPresentationStore();
        using var presenter = new WebUiPermissionPresenter(
            store, () => registry,
            TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(400),
            NullLogger<WebUiPermissionPresenter>.Instance);
        var reports = new List<IReadOnlyCollection<string>>();
        presenter.SurfacedChanged += () => reports.Add(presenter.SurfaceSessionIds.ToArray());

        registry.Rebind(string.Empty);       // 瞬态空集候选
        await Task.Delay(40);                // 已过去抖，但未到 400ms 确认期
        registry.Rebind("anchor");           // 期内恢复非空

        await Task.Delay(200);

        presenter.SurfaceSessionIds.Should().Contain("anchor");
        reports.Should().NotContain(r => r.Count == 0);
    }

    [Fact]
    public async Task Dispose_ShouldUnregisterAndStopReporting()
    {
        var registry = await ReadyRegistryAsync();
        var store = new FakePermissionPresentationStore();
        var presenter = new WebUiPermissionPresenter(
            store, () => registry,
            TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(100),
            NullLogger<WebUiPermissionPresenter>.Instance);
        var reports = 0;
        presenter.SurfacedChanged += () => Interlocked.Increment(ref reports);

        presenter.Dispose();

        store.UnregisterCount.Should().Be(1);
        registry.Rebind("other");
        await Task.Delay(120);

        reports.Should().Be(0);
        presenter.SurfaceSessionIds.Should().BeEmpty();
    }

    [Fact]
    public void Prerender_NullRegistry_ShouldNotRegister()
    {
        var store = new FakePermissionPresentationStore();
        using var presenter = new WebUiPermissionPresenter(
            store, () => null,
            TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10),
            NullLogger<WebUiPermissionPresenter>.Instance);

        store.RegisterCount.Should().Be(0);
        presenter.SurfaceSessionIds.Should().BeEmpty();
    }
}

/// <summary>测试替身：呈现端登记表。</summary>
internal sealed class FakePermissionPresentationStore : IPermissionPresentationStore
{
    private readonly HashSet<IPermissionPresenter> _presenters = new();

    public int RegisterCount { get; private set; }

    public int UnregisterCount { get; private set; }

    public event Action? PresenterUnregistered;

    public event Action? Changed;

    public void Register(IPermissionPresenter presenter)
    {
        if (_presenters.Add(presenter))
            RegisterCount++;
        Changed?.Invoke();
    }

    public void Unregister(IPermissionPresenter presenter)
    {
        if (_presenters.Remove(presenter))
            UnregisterCount++;
        PresenterUnregistered?.Invoke();
        Changed?.Invoke();
    }

    public bool CanSurface(string sessionId)
        => _presenters.Any(p => p.SurfaceSessionIds.Contains(sessionId));
}
