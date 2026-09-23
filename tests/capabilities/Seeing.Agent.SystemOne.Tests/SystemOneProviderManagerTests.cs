using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.SystemOne;
using Seeing.Agent.SystemOne.Configuration;
using Xunit;

namespace Seeing.Agent.SystemOne.Tests;

public class SystemOneProviderManagerTests
{
    private static SystemOneProviderManager CreateManager(
        Dictionary<string, SystemOneProviderConfig>? configs = null,
        IEnumerable<ISystemOneClientFactory>? factories = null)
    {
        var store = new SystemOneConfigStore(new TestConfigSectionStore(
            configs ?? new Dictionary<string, SystemOneProviderConfig>()));
        return new SystemOneProviderManager(
            store,
            factories ?? new ISystemOneClientFactory[] { new StubSystemOneClientFactory() },
            NullLogger<SystemOneProviderManager>.Instance);
    }

    [Fact]
    public void Register_同Id去重且释放被替换实例()
    {
        using var manager = CreateManager();
        var first = new StubSystemOneProvider { Id = "x" };
        var second = new StubSystemOneProvider { Id = "x" };

        manager.Register(first);
        manager.Register(second);

        manager.Providers.Should().ContainSingle().Which.Should().BeSameAs(second);
        first.Disposed.Should().BeTrue();
    }

    [Fact]
    public void Unregister_移除并释放_返回True()
    {
        using var manager = CreateManager();
        var provider = new StubSystemOneProvider { Id = "x" };
        manager.Register(provider);

        manager.Unregister("x").Should().BeTrue();
        manager.Providers.Should().BeEmpty();
        provider.Disposed.Should().BeTrue();
        manager.Unregister("x").Should().BeFalse();
    }

    [Fact]
    public void UnregisterByOwner_仅移除该Owner()
    {
        using var manager = CreateManager();
        var a = new StubSystemOneProvider { Id = "a" };
        var b = new StubSystemOneProvider { Id = "b" };
        manager.Register(a, ownerExtensionId: "ext-a");
        manager.Register(b, ownerExtensionId: "ext-b");

        manager.UnregisterByOwner("ext-a").Should().Be(1);

        manager.Providers.Select(p => p.Id).Should().Equal("b");
        a.Disposed.Should().BeTrue();
        b.Disposed.Should().BeFalse();
    }

    [Fact]
    public void ProvidersChanged_应在注册与注销时触发()
    {
        using var manager = CreateManager();
        var count = 0;
        manager.ProvidersChanged += (_, _) => count++;

        manager.Register(new StubSystemOneProvider { Id = "x" });
        manager.Unregister("x");

        count.Should().Be(2);
    }

    [Fact]
    public void Reload_无ApiKey_不注册Provider()
    {
        using var env = SystemOneTestEnv.Scope();
        using var manager = CreateManager();

        manager.Reload();

        manager.Providers.Should().BeEmpty();
        manager.DefaultProvider.Should().BeNull();
    }

    [Fact]
    public void Reload_未知类型_跳过并告警()
    {
        using var env = SystemOneTestEnv.Scope(apiKey: "k");
        using var manager = CreateManager(new Dictionary<string, SystemOneProviderConfig>
        {
            ["weird"] = new() { Id = "weird", Type = "weird", ApiKey = "k" }
        });

        manager.Reload();

        manager.Providers.Select(p => p.Id).Should().Equal("typesafe");
    }

    [Fact]
    public void Reload_环境变量_覆盖内置TypeSafe()
    {
        using var env = SystemOneTestEnv.Scope(
            apiKey: "env-key", baseUrl: "https://env.example", model: "env-model");
        var factory = new StubSystemOneClientFactory();
        using var manager = CreateManager(factories: new ISystemOneClientFactory[] { factory });

        manager.Reload();

        var provider = manager.Providers.Should().ContainSingle().Subject;
        provider.Id.Should().Be("typesafe");
        _ = provider.GetClient();

        var captured = factory.CreatedConfigs.Should().ContainSingle().Subject;
        captured.ApiKey.Should().Be("env-key");
        captured.BaseUrl.Should().Be("https://env.example");
        captured.Model.Should().Be("env-model");
    }

    [Fact]
    public void Reload_保留外部Register的Provider()
    {
        using var env = SystemOneTestEnv.Scope(apiKey: "k");
        using var manager = CreateManager();
        var external = new StubSystemOneProvider { Id = "external" };
        manager.Register(external);

        manager.Reload();

        manager.Providers.Select(p => p.Id).Should().Contain(new[] { "external", "typesafe" });
        manager.Providers.Should().ContainSingle(p => p.Id == "external").Which.Should().BeSameAs(external);
        external.Disposed.Should().BeFalse();
    }

    [Fact]
    public void Reload_仅重建本模块Owner条目()
    {
        using var env = SystemOneTestEnv.Scope(apiKey: "k");
        using var manager = CreateManager();
        var external = new StubSystemOneProvider { Id = "external" };
        manager.Register(external);

        manager.Reload();
        var firstTypesafe = manager.Providers.Single(p => p.Id == "typesafe");
        manager.Reload();
        var secondTypesafe = manager.Providers.Single(p => p.Id == "typesafe");

        secondTypesafe.Should().NotBeSameAs(firstTypesafe);
        manager.Providers.Single(p => p.Id == "external").Should().BeSameAs(external);
        manager.Providers.Should().HaveCount(2);
    }

    [Fact]
    public void Reload_外部同Id优先_跳过内置()
    {
        using var env = SystemOneTestEnv.Scope(apiKey: "k");
        using var manager = CreateManager();
        var external = new StubSystemOneProvider { Id = "typesafe" };
        manager.Register(external);

        manager.Reload();

        manager.Providers.Should().ContainSingle().Which.Should().BeSameAs(external);
        external.Disposed.Should().BeFalse();
    }

    [Fact]
    public void Dispose_释放全部Provider()
    {
        var manager = CreateManager();
        var provider = new StubSystemOneProvider { Id = "x" };
        manager.Register(provider);

        manager.Dispose();

        provider.Disposed.Should().BeTrue();
        manager.Providers.Should().BeEmpty();
    }

    [Fact]
    public void Dispose_后调用Reload_应抛ObjectDisposedException()
    {
        var manager = CreateManager();
        manager.Dispose();

        var act = () => manager.Reload();

        act.Should().Throw<ObjectDisposedException>()
            .Which.ObjectName.Should().Be(nameof(SystemOneProviderManager));
    }

    [Fact]
    public void Dispose_后调用Register_应抛ObjectDisposedException()
    {
        var manager = CreateManager();
        manager.Dispose();

        var act = () => manager.Register(new StubSystemOneProvider { Id = "x" });

        act.Should().Throw<ObjectDisposedException>()
            .Which.ObjectName.Should().Be(nameof(SystemOneProviderManager));
    }

    [Fact]
    public void Dispose_后调用Unregister_应抛ObjectDisposedException()
    {
        var manager = CreateManager();
        manager.Dispose();

        var act = () => manager.Unregister("x");

        act.Should().Throw<ObjectDisposedException>()
            .Which.ObjectName.Should().Be(nameof(SystemOneProviderManager));
    }

    [Fact]
    public void Dispose_后调用UnregisterByOwner_应抛ObjectDisposedException()
    {
        var manager = CreateManager();
        manager.Dispose();

        var act = () => manager.UnregisterByOwner("systemone");

        act.Should().Throw<ObjectDisposedException>()
            .Which.ObjectName.Should().Be(nameof(SystemOneProviderManager));
    }

    [Fact]
    public void Reload_字典键与id不一致且id为typesafe_不产生重复Id()
    {
        using var env = SystemOneTestEnv.Scope(apiKey: "k");
        using var manager = CreateManager(new Dictionary<string, SystemOneProviderConfig>
        {
            ["alias"] = new() { Id = "typesafe", Type = SystemOneProviderTypes.TypeSafe, ApiKey = "k" }
        });

        manager.Reload();

        manager.Providers.Should().ContainSingle().Which.Id.Should().Be("typesafe");
        manager.Providers.Should().NotContain(p => p.Id == "alias");
    }
}
