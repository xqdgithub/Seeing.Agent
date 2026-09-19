using FluentAssertions;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Core.Permission;
using Xunit;

namespace Seeing.Agent.Tests.Permission;

/// <summary>
/// 呈现端登记表测试（spec §4.1、§11.10）：Register/Unregister 幂等、CanSurface 并集、
/// Changed/PresenterUnregistered 触发、SurfacedChanged 转发、异常隔离。
/// </summary>
public class PermissionPresentationStoreTests
{
    [Fact]
    public void Register_ThenCanSurface_ShouldBeTrue()
    {
        var store = new PermissionPresentationStore();
        var presenter = new TestPresenter("s1");

        store.CanSurface("s1").Should().BeFalse();
        store.Register(presenter);

        store.CanSurface("s1").Should().BeTrue();
    }

    [Fact]
    public void Unregister_ShouldRemovePresenter()
    {
        var store = new PermissionPresentationStore();
        var presenter = new TestPresenter("s1");

        store.Register(presenter);
        store.Unregister(presenter);

        store.CanSurface("s1").Should().BeFalse();
        var act = () => store.Unregister(presenter);
        act.Should().NotThrow();
    }

    [Fact]
    public void Register_Twice_ShouldBeIdempotent()
    {
        var store = new PermissionPresentationStore();
        var presenter = new TestPresenter("s1");
        var changed = 0;
        store.Changed += () => changed++;

        store.Register(presenter);
        store.Register(presenter);

        changed.Should().Be(1);
        store.CanSurface("s1").Should().BeTrue();
    }

    [Fact]
    public void CanSurface_MultiplePresenters_ShouldBeUnion()
    {
        var store = new PermissionPresentationStore();
        store.Register(new TestPresenter("s1"));
        store.Register(new TestPresenter("s2"));

        store.CanSurface("s1").Should().BeTrue();
        store.CanSurface("s2").Should().BeTrue();
        store.CanSurface("s3").Should().BeFalse();
    }

    [Fact]
    public void CanSurface_EmptyOrNullSession_ShouldBeFalse()
    {
        var store = new PermissionPresentationStore();
        store.Register(new TestPresenter("s1", ""));

        store.CanSurface("").Should().BeFalse();
        store.CanSurface(null!).Should().BeFalse();
    }

    [Fact]
    public void Register_ShouldRaiseChangedButNotPresenterUnregistered()
    {
        var store = new PermissionPresentationStore();
        var changed = 0;
        var unregistered = 0;
        store.Changed += () => changed++;
        store.PresenterUnregistered += () => unregistered++;

        store.Register(new TestPresenter("s1"));

        changed.Should().Be(1);
        unregistered.Should().Be(0);
    }

    [Fact]
    public void Unregister_ShouldRaiseChangedAndPresenterUnregistered()
    {
        var store = new PermissionPresentationStore();
        var presenter = new TestPresenter("s1");
        store.Register(presenter);

        var changed = 0;
        var unregistered = 0;
        store.Changed += () => changed++;
        store.PresenterUnregistered += () => unregistered++;

        store.Unregister(presenter);

        changed.Should().Be(1);
        unregistered.Should().Be(1);
    }

    [Fact]
    public void PresenterSurfacedChanged_ShouldRaiseChangedOnly()
    {
        var store = new PermissionPresentationStore();
        var presenter = new TestPresenter("s1");
        store.Register(presenter);

        var changed = 0;
        var unregistered = 0;
        store.Changed += () => changed++;
        store.PresenterUnregistered += () => unregistered++;

        presenter.SetSurface("s1", "s2");

        changed.Should().Be(1);
        unregistered.Should().Be(0);
    }

    [Fact]
    public void UnregisteredPresenterSurfacedChanged_ShouldNotRaiseChanged()
    {
        var store = new PermissionPresentationStore();
        var presenter = new TestPresenter("s1");
        store.Register(presenter);
        store.Unregister(presenter);

        var changed = 0;
        store.Changed += () => changed++;
        presenter.SetSurface("s1", "s2");

        changed.Should().Be(0);
    }

    [Fact]
    public void SubscriberThrows_ShouldNotBreakRegister()
    {
        var store = new PermissionPresentationStore();
        store.Changed += () => throw new InvalidOperationException("boom");

        var act = () => store.Register(new TestPresenter("s1"));

        act.Should().NotThrow();
    }

    private sealed class TestPresenter : IPermissionPresenter
    {
        private IReadOnlyCollection<string> _surface;

        public TestPresenter(params string[] sessionIds) => _surface = sessionIds;

        public IReadOnlyCollection<string> SurfaceSessionIds => _surface;

        public event Action? SurfacedChanged;

        public void SetSurface(params string[] sessionIds)
        {
            _surface = sessionIds;
            SurfacedChanged?.Invoke();
        }
    }
}
