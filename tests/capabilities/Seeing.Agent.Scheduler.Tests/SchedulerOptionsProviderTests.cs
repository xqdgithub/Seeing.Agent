using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Scheduler.Configuration;
using Seeing.Agent.Scheduler.Models;
using Seeing.Agent.Scheduler.Tests.Fixtures;
using Xunit;

namespace Seeing.Agent.Scheduler.Tests;

public class SchedulerOptionsProviderTests
{
    [Fact]
    public void Reload_LoadsSchedulerSectionFromProjectSeeingJson()
    {
        using var ws = new SchedulerTestWorkspace();
        ws.WriteSeeingJson(new SchedulerOptions
        {
            Enabled = true,
            Timezone = "Asia/Shanghai"
        });

        var provider = ws.CreateOptionsProvider();

        provider.Current.Enabled.Should().BeTrue();
        provider.Current.Timezone.Should().Be("Asia/Shanghai");
    }

    [Fact]
    public void Reload_ProjectOverridesDefaults()
    {
        using var ws = new SchedulerTestWorkspace();
        ws.WriteSeeingJson(new SchedulerOptions { Enabled = false });

        var provider = ws.CreateOptionsProvider();
        provider.Current.Enabled.Should().BeFalse();
    }

    [Fact]
    public void ReloadHandler_声明订阅配置变更类型()
    {
        using var ws = new SchedulerTestWorkspace();
        var (_, _, handler) = CreateReloadHandler(ws);

        handler.ChangeTypes.Should().Contain(typeof(ConfigChange));
    }

    [Fact]
    public async Task SchedulerReloadHandler_配置变更重载()
    {
        using var ws = new SchedulerTestWorkspace();
        var (configManager, provider, handler) = CreateReloadHandler(ws);

        var newOptions = new SchedulerOptions
        {
            Enabled = true,
            Timezone = "Asia/Shanghai",
            MaxConcurrentJobs = 5
        };
        await configManager.SaveSectionAsync("Scheduler", newOptions, ConfigLevel.Project, TestContext.Current.CancellationToken);

        await handler.ReloadAsync(new ConfigChange { ChangedSections = new[] { "Scheduler" } }, TestContext.Current.CancellationToken);

        provider.Current.Enabled.Should().BeTrue();
        provider.Current.Timezone.Should().Be("Asia/Shanghai");
        provider.Current.MaxConcurrentJobs.Should().Be(5);
    }

    [Fact]
    public async Task SchedulerReloadHandler_无关配置节不触发重载()
    {
        using var ws = new SchedulerTestWorkspace();
        var (configManager, provider, handler) = CreateReloadHandler(ws);

        var newOptions = new SchedulerOptions { Enabled = false };
        await configManager.SaveSectionAsync("Scheduler", newOptions, ConfigLevel.Project, TestContext.Current.CancellationToken);

        await handler.ReloadAsync(new ConfigChange { ChangedSections = new[] { "Memory" } }, TestContext.Current.CancellationToken);

        provider.Current.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task SchedulerReloadHandler_空配置节全量重载()
    {
        using var ws = new SchedulerTestWorkspace();
        var (configManager, provider, handler) = CreateReloadHandler(ws);

        var newOptions = new SchedulerOptions { Enabled = false };
        await configManager.SaveSectionAsync("Scheduler", newOptions, ConfigLevel.Project, TestContext.Current.CancellationToken);

        await handler.ReloadAsync(new ConfigChange(), TestContext.Current.CancellationToken);

        provider.Current.Enabled.Should().BeFalse();
    }

    private static (UnifiedConfigManager ConfigManager, SchedulerOptionsProvider Provider, SchedulerReloadHandler Handler) CreateReloadHandler(
        SchedulerTestWorkspace ws)
    {
        var configManager = new UnifiedConfigManager(ws.Workspace, NullLogger<UnifiedConfigManager>.Instance, CreateSchedulerRegistry());
        var provider = new SchedulerOptionsProvider(configManager, NullLogger<SchedulerOptionsProvider>.Instance);
        var handler = new SchedulerReloadHandler(provider);
        return (configManager, provider, handler);
    }

    private static ConfigSectionRegistry CreateSchedulerRegistry()
    {
        var registry = ConfigSectionRegistry.CreateWithSpine();
        registry.Register(new ConfigSectionMeta(
            "Scheduler", "scheduler.json", ConfigScope.ProjectOnly, typeof(SchedulerOptions)));
        return registry;
    }
}
