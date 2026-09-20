using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Interactions;
using Seeing.Agent.Core.Execution;
using Seeing.Agent.WebUI.Services;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

public class WebUiSurfacePresenterTests
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
        var store = new FakePermissionSurfaceRegistry();
        var questionStore = new FakePermissionSurfaceRegistry();

        using var presenter = new WebUiSurfacePresenter(
            store, questionStore, () => registry,
            TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(200),
            NullLogger<WebUiSurfacePresenter>.Instance);

        presenter.SurfaceSessionIds.Should().BeEquivalentTo(new[] { "anchor", "child" });
        store.RegisterCount.Should().Be(1);
        questionStore.RegisterCount.Should().Be(1);
        store.CanSurface("child").Should().BeTrue();
        questionStore.CanSurface("child").Should().BeTrue();
    }

    [Fact]
    public async Task TransientEmptyWithinConfirmWindow_ShouldNotReportEmpty()
    {
        var registry = await ReadyRegistryAsync();
        var store = new FakePermissionSurfaceRegistry();
        var questionStore = new FakePermissionSurfaceRegistry();
        using var presenter = new WebUiSurfacePresenter(
            store, questionStore, () => registry,
            TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(400),
            NullLogger<WebUiSurfacePresenter>.Instance);
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
        var store = new FakePermissionSurfaceRegistry();
        var questionStore = new FakePermissionSurfaceRegistry();
        var presenter = new WebUiSurfacePresenter(
            store, questionStore, () => registry,
            TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(100),
            NullLogger<WebUiSurfacePresenter>.Instance);
        var reports = 0;
        presenter.SurfacedChanged += () => Interlocked.Increment(ref reports);

        presenter.Dispose();

        store.UnregisterCount.Should().Be(1);
        questionStore.UnregisterCount.Should().Be(1);
        registry.Rebind("other");
        await Task.Delay(120);

        reports.Should().Be(0);
        presenter.SurfaceSessionIds.Should().BeEmpty();
    }

    [Fact]
    public void Prerender_NullRegistry_ShouldNotRegister()
    {
        var store = new FakePermissionSurfaceRegistry();
        var questionStore = new FakePermissionSurfaceRegistry();
        using var presenter = new WebUiSurfacePresenter(
            store, questionStore, () => null,
            TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10),
            NullLogger<WebUiSurfacePresenter>.Instance);

        store.RegisterCount.Should().Be(0);
        questionStore.RegisterCount.Should().Be(0);
        presenter.SurfaceSessionIds.Should().BeEmpty();
    }
}

/// <summary>测试替身：可呈现性注册表（同时满足权限与 Question 两个 marker 接口）。</summary>
internal sealed class FakePermissionSurfaceRegistry : IPermissionSurfaceRegistry, IQuestionSurfaceRegistry
{
    private readonly HashSet<ISurfaceProvider> _providers = new();

    public int RegisterCount { get; private set; }

    public int UnregisterCount { get; private set; }

    public event Action? ProviderUnregistered;

    public event Action? Changed;

    public void Register(ISurfaceProvider provider)
    {
        if (_providers.Add(provider))
            RegisterCount++;
        Changed?.Invoke();
    }

    public void Unregister(ISurfaceProvider provider)
    {
        if (_providers.Remove(provider))
            UnregisterCount++;
        ProviderUnregistered?.Invoke();
        Changed?.Invoke();
    }

    public bool CanSurface(string sessionId)
        => _providers.Any(p => p.SurfaceSessionIds.Contains(sessionId));
}
