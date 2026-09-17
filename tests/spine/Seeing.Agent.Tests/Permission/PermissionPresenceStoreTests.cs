using FluentAssertions;
using Seeing.Agent.Core.Permission;
using Xunit;

namespace Seeing.Agent.Tests.Permission;

/// <summary>
/// 会话可交互计数测试（spec §11.5）：attach/detach、多会话隔离、并发计数、ClearAll。
/// </summary>
public class PermissionPresenceStoreTests
{
    [Fact]
    public void Attach_ThenCanPresent_ShouldBeTrue()
    {
        var store = new PermissionPresenceStore();

        store.CanPresent("s1").Should().BeFalse();
        store.Attach("s1");
        store.CanPresent("s1").Should().BeTrue();
    }

    [Fact]
    public void Detach_ShouldDecrementCount_AndRemoveAtZero()
    {
        var store = new PermissionPresenceStore();

        store.Attach("s1");
        store.Attach("s1");
        store.CanPresent("s1").Should().BeTrue();

        store.Detach("s1");
        store.CanPresent("s1").Should().BeTrue();

        store.Detach("s1");
        store.CanPresent("s1").Should().BeFalse();

        var act = () => store.Detach("s1");
        act.Should().NotThrow();
        store.CanPresent("s1").Should().BeFalse();
    }

    [Fact]
    public void CanPresent_UnknownOrEmptySession_ShouldBeFalse()
    {
        var store = new PermissionPresenceStore();

        store.CanPresent("unknown").Should().BeFalse();
        store.CanPresent(string.Empty).Should().BeFalse();

        store.Attach(string.Empty);
        store.CanPresent(string.Empty).Should().BeFalse();
        var act = () => store.Detach(string.Empty);
        act.Should().NotThrow();
    }

    [Fact]
    public void MultiSession_ShouldBeIsolated()
    {
        var store = new PermissionPresenceStore();

        store.Attach("s1");
        store.Attach("s2");

        store.Detach("s1");

        store.CanPresent("s1").Should().BeFalse();
        store.CanPresent("s2").Should().BeTrue();
    }

    [Fact]
    public void ClearAll_ShouldDropEverySession()
    {
        var store = new PermissionPresenceStore();
        store.Attach("s1");
        store.Attach("s2");
        store.Attach("s2");

        store.ClearAll();

        store.CanPresent("s1").Should().BeFalse();
        store.CanPresent("s2").Should().BeFalse();
        store.Attach("s2");
        store.CanPresent("s2").Should().BeTrue();
    }

    [Fact]
    public void ConcurrentAttachAndDetach_ShouldKeepRefCountConsistent()
    {
        var store = new PermissionPresenceStore();

        Parallel.For(0, 1000, _ => store.Attach("s1"));
        store.CanPresent("s1").Should().BeTrue();

        Parallel.For(0, 1000, _ => store.Detach("s1"));
        store.CanPresent("s1").Should().BeFalse();

        Parallel.For(0, 500, _ => store.Attach("s2"));
        Parallel.For(0, 499, _ => store.Detach("s2"));
        store.CanPresent("s2").Should().BeTrue();

        store.Detach("s2");
        store.CanPresent("s2").Should().BeFalse();
    }
}
