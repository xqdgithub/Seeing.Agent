using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Configuration;
using Xunit;

namespace Seeing.Agent.Tests.Configuration;

public class ReloadOrchestratorTests
{
    private sealed class TrackingHandler : IReloadHandler
    {
        public string ComponentId { get; set; } = "track";
        public IReadOnlyList<Type> ChangeTypes { get; } = new[] { typeof(ConfigChange) };
        public List<string> Received { get; } = new();
        public Func<ConfigChange, Task>? OnConfig;
        public Task ReloadAsync(IReloadSignal change, CancellationToken ct)
        {
            if (change is ConfigChange cfg)
            {
                lock (Received) Received.Add(string.Join(",", cfg.ChangedSections));
                return OnConfig?.Invoke(cfg) ?? Task.CompletedTask;
            }
            return Task.CompletedTask;
        }
    }

    private static (ReloadOrchestrator orch, TrackingHandler handler) CreateOrchestrator(
        out Mock<IConfigSectionStore> configStore, out Mock<IWorkspaceProvider> workspace)
    {
        configStore = new Mock<IConfigSectionStore>();
        workspace = new Mock<IWorkspaceProvider>();
        var handler = new TrackingHandler();
        var orch = new ReloadOrchestrator(
            new[] { handler }, configStore.Object, workspace.Object,
            NullLogger<ReloadOrchestrator>.Instance);
        return (orch, handler);
    }

    [Fact]
    public async Task ConfigChanged_路由到匹配Handler()
    {
        var (orch, handler) = CreateOrchestrator(out var configStore, out var workspace);
        configStore.Raise(x => x.ConfigChanged += null, new ConfigChangedEventArgs { ChangedSections = new[] { "Providers" } });

        await Task.Delay(200); // 等待异步调度
        handler.Received.Should().Contain("Providers");
    }

    [Fact]
    public async Task WorkspaceChanged_构造WorkspaceChange()
    {
        var (orch, handler) = CreateOrchestrator(out var configStore, out var workspace);
        workspace.Raise(x => x.WorkspaceRootChanged += null, new WorkspaceChangedEventArgs { OldWorkspace = "/old", NewWorkspace = "/new" });

        await Task.Delay(200);
        handler.Received.Should().NotBeEmpty();
    }

    [Fact]
    public async Task 失败隔离_一个Handler异常不影响其他()
    {
        var bad = new TrackingHandler { ComponentId = "bad", OnConfig = _ => throw new InvalidOperationException("boom") };
        var good = new TrackingHandler();
        var orch = new ReloadOrchestrator(
            new IReloadHandler[] { bad, good },
            new Mock<IConfigSectionStore>().Object, new Mock<IWorkspaceProvider>().Object,
            NullLogger<ReloadOrchestrator>.Instance);

        orch.ReloadAsync(new ConfigChange { ChangedSections = new[] { "X" } }).GetAwaiter().GetResult();

        good.Received.Should().Contain("X");
    }

    [Fact]
    public async Task 显式ReloadAsync_返回结果集合()
    {
        var (orch, _) = CreateOrchestrator(out _, out _);
        var results = await orch.ReloadAsync(new ConfigChange { ChangedSections = new[] { "X" } });
        results.Should().HaveCount(1);
        results[0].ComponentId.Should().Be("track");
        results[0].Success.Should().BeTrue();
    }

    [Fact]
    public async Task 去抖_连续全量变更合并为一次()
    {
        var (orch, handler) = CreateOrchestrator(out var configStore, out _);
        var args = new ConfigChangedEventArgs { ChangedSections = Array.Empty<string>() };

        configStore.Raise(x => x.ConfigChanged += null, args);
        configStore.Raise(x => x.ConfigChanged += null, args);
        configStore.Raise(x => x.ConfigChanged += null, args);

        await Task.Delay(500);
        lock (handler.Received) handler.Received.Should().HaveCount(1);
    }

    [Fact]
    public async Task PublishAsync_即ReloadAsync_插件推送可用()
    {
        var (orch, handler) = CreateOrchestrator(out _, out _);
        var bus = (IReloadSignalBus)orch;
        var results = await bus.PublishAsync(new ConfigChange { ChangedSections = new[] { "P" } });
        results.Should().HaveCount(1);
        handler.Received.Should().Contain("P");
    }

    [Fact]
    public async Task 动态注册_RegisterHandler_后路由生效()
    {
        var (orch, _) = CreateOrchestrator(out _, out _);
        var registry = (IReloadHandlerRegistry)orch;
        var pluginHandler = new TrackingHandler { ComponentId = "plugin" };
        registry.RegisterHandler(pluginHandler);

        var results = await orch.ReloadAsync(new ConfigChange { ChangedSections = new[] { "X" } });
        results.Select(r => r.ComponentId).Should().Contain("plugin");
        pluginHandler.Received.Should().Contain("X");

        registry.UnregisterHandler(pluginHandler);
        results = await orch.ReloadAsync(new ConfigChange { ChangedSections = new[] { "Y" } });
        results.Select(r => r.ComponentId).Should().NotContain("plugin");
    }
}