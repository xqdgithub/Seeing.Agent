using FluentAssertions;
using Seeing.Agent.Abstractions.Interactions;
using Seeing.Agent.Core.Interactions;
using Xunit;

namespace Seeing.Agent.Tests.Interactions;

/// <summary>
/// 可呈现性注册表测试（spec §5.1）：Register/Unregister 幂等、CanSurface 并集、
/// ProviderUnregistered/Changed 触发顺序、SurfacedChanged 转发、读取异常隔离。
/// </summary>
public class SurfaceRegistryTests
{
    [Fact]
    public void Register_ThenCanSurface_ShouldBeTrue()
    {
        var registry = new SurfaceRegistry();
        var provider = new FakePresenter("s1");

        registry.CanSurface("s1").Should().BeFalse();
        registry.Register(provider);

        registry.CanSurface("s1").Should().BeTrue();
    }

    [Fact]
    public void Register_Twice_ShouldBeIdempotent()
    {
        var registry = new SurfaceRegistry();
        var provider = new FakePresenter("s1");
        var changed = 0;
        registry.Changed += () => changed++;

        registry.Register(provider);
        registry.Register(provider);

        changed.Should().Be(1);
        registry.CanSurface("s1").Should().BeTrue();
    }

    [Fact]
    public void Unregister_ShouldBeIdempotent()
    {
        var registry = new SurfaceRegistry();
        var provider = new FakePresenter("s1");
        registry.Register(provider);

        registry.Unregister(provider);
        registry.Unregister(provider);

        registry.CanSurface("s1").Should().BeFalse();
    }

    [Fact]
    public void Unregister_ShouldRaiseProviderUnregisteredBeforeChanged()
    {
        var registry = new SurfaceRegistry();
        var provider = new FakePresenter("s1");
        registry.Register(provider);

        var order = new List<string>();
        registry.ProviderUnregistered += () => order.Add("unregistered");
        registry.Changed += () => order.Add("changed");

        registry.Unregister(provider);

        order.Should().ContainInOrder("unregistered", "changed");
    }

    [Fact]
    public void Register_ShouldRaiseChangedOnly()
    {
        var registry = new SurfaceRegistry();
        var order = new List<string>();
        registry.ProviderUnregistered += () => order.Add("unregistered");
        registry.Changed += () => order.Add("changed");

        registry.Register(new FakePresenter("s1"));

        order.Should().ContainSingle().Which.Should().Be("changed");
    }

    [Fact]
    public void CanSurface_MultipleProviders_ShouldBeUnion()
    {
        var registry = new SurfaceRegistry();
        registry.Register(new FakePresenter("s1"));
        registry.Register(new FakePresenter("s2"));

        registry.CanSurface("s1").Should().BeTrue();
        registry.CanSurface("s2").Should().BeTrue();
        registry.CanSurface("s3").Should().BeFalse();
    }

    [Fact]
    public void CanSurface_EmptyOrWhitespaceSession_ShouldBeFalse()
    {
        var registry = new SurfaceRegistry();
        registry.Register(new FakePresenter("s1", ""));

        registry.CanSurface("").Should().BeFalse();
        registry.CanSurface("   ").Should().BeFalse();
        registry.CanSurface(null!).Should().BeFalse();
    }

    [Fact]
    public void CanSurface_WhenProviderThrows_ShouldUseOthersWithoutThrowing()
    {
        var registry = new SurfaceRegistry();
        registry.Register(new FakePresenter("s1") { ThrowOnRead = true });
        registry.Register(new FakePresenter("s2"));

        var act = () => registry.CanSurface("s2");

        act.Should().NotThrow().Which.Should().BeTrue();
        registry.CanSurface("s1").Should().BeFalse();
    }

    [Fact]
    public void ProviderSurfacedChanged_ShouldRaiseChanged()
    {
        var registry = new SurfaceRegistry();
        var provider = new FakePresenter("s1");
        registry.Register(provider);

        var changed = 0;
        registry.Changed += () => changed++;

        provider.SetSurface("s1", "s2");

        changed.Should().Be(1);
    }

    [Fact]
    public void UnregisteredProviderSurfacedChanged_ShouldNotRaiseChanged()
    {
        var registry = new SurfaceRegistry();
        var provider = new FakePresenter("s1");
        registry.Register(provider);
        registry.Unregister(provider);

        var changed = 0;
        registry.Changed += () => changed++;
        provider.SetSurface("s1", "s2");

        changed.Should().Be(0);
    }

    private sealed class FakePresenter : ISurfaceProvider
    {
        private IReadOnlyCollection<string> _surface;

        public FakePresenter(params string[] sessionIds) => _surface = sessionIds;

        public bool ThrowOnRead { get; set; }

        public IReadOnlyCollection<string> SurfaceSessionIds => ThrowOnRead
            ? throw new InvalidOperationException("boom")
            : _surface;

        public event Action? SurfacedChanged;

        public void SetSurface(params string[] sessionIds)
        {
            _surface = sessionIds;
            SurfacedChanged?.Invoke();
        }
    }
}
