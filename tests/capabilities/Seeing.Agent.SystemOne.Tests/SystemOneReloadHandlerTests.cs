using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.SystemOne;
using Seeing.Agent.SystemOne.Configuration;
using Xunit;

namespace Seeing.Agent.SystemOne.Tests;

public class SystemOneReloadHandlerTests
{
    private static SystemOneProviderManager CreateManager()
        => new(
            new SystemOneConfigStore(new TestConfigSectionStore()),
            new ISystemOneClientFactory[] { new StubSystemOneClientFactory() },
            NullLogger<SystemOneProviderManager>.Instance);

    [Fact]
    public async Task ReloadAsync_包含SystemOne节_应触发重建()
    {
        using var env = SystemOneTestEnv.Scope(apiKey: "k");
        using var manager = CreateManager();
        var handler = new SystemOneReloadHandler(manager);
        var changed = 0;
        manager.ProvidersChanged += (_, _) => changed++;

        await handler.ReloadAsync(
            new ConfigChange { ChangedSections = new[] { "SystemOne" } },
            TestContext.Current.CancellationToken);

        changed.Should().Be(1);
        manager.Providers.Should().ContainSingle(p => p.Id == "typesafe");
    }

    [Fact]
    public async Task ReloadAsync_空节列表_应全量重建()
    {
        using var env = SystemOneTestEnv.Scope(apiKey: "k");
        using var manager = CreateManager();
        var handler = new SystemOneReloadHandler(manager);
        var changed = 0;
        manager.ProvidersChanged += (_, _) => changed++;

        await handler.ReloadAsync(
            new ConfigChange { ChangedSections = Array.Empty<string>() },
            TestContext.Current.CancellationToken);

        changed.Should().Be(1);
    }

    [Fact]
    public async Task ReloadAsync_其他节_不应重建()
    {
        using var env = SystemOneTestEnv.Scope(apiKey: "k");
        using var manager = CreateManager();
        var handler = new SystemOneReloadHandler(manager);
        var changed = 0;
        manager.ProvidersChanged += (_, _) => changed++;

        await handler.ReloadAsync(
            new ConfigChange { ChangedSections = new[] { "Other" } },
            TestContext.Current.CancellationToken);

        changed.Should().Be(0);
    }

    [Fact]
    public void ComponentId_应为systemone()
    {
        using var manager = CreateManager();

        new SystemOneReloadHandler(manager).ComponentId.Should().Be("systemone");
    }
}
