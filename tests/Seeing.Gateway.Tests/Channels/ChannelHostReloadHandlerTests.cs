using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Extensions;
using Seeing.Agent.Gateway.Channels;
using Seeing.Agent.Gateway.Hosting;
using Xunit;

namespace Seeing.Gateway.Tests.Channels;

/// <summary>
/// ChannelHostReloadHandler 配置变更协调测试。
/// 说明：ChannelHostManager 为密封具体类（方法非虚），无法直接 mock 验证启停调用；
/// 因此通过真实管理器产生的可观察日志/副作用验证 StartAsync 被按启用状态调度。
/// </summary>
public class ChannelHostReloadHandlerTests : IDisposable
{
    private readonly string _tempDirectory =
        Path.Combine(Path.GetTempPath(), "gateway-channelhost-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<IWorkspaceProvider> _workspace;
    private readonly Mock<IConfigSectionStore> _sectionStore;
    private readonly Mock<IGatewayServer> _gatewayServer;
    private readonly ListLogger<ChannelHostHostedService> _serviceLogger;
    private readonly ListLogger<ChannelHostManager> _managerLogger;
    private readonly ChannelHostReloadHandler _handler;

    public ChannelHostReloadHandlerTests()
    {
        Directory.CreateDirectory(_tempDirectory);

        _workspace = new Mock<IWorkspaceProvider>();
        _workspace.Setup(x => x.WorkspaceRoot).Returns(_tempDirectory);
        _workspace.Setup(x => x.ProjectSeeingDirectory).Returns(Path.Combine(_tempDirectory, ".seeing"));
        _workspace.Setup(x => x.UserSeeingDirectory).Returns(Path.Combine(_tempDirectory, "user", ".seeing"));

        var configManager = new UnifiedConfigManager(_workspace.Object, NullLogger<UnifiedConfigManager>.Instance);
        var registry = new GatewayChannelRegistry(
            NullLogger<GatewayChannelRegistry>.Instance,
            new ExtensionLoader(NullLogger<ExtensionLoader>.Instance),
            configManager);
        registry.Reload(_tempDirectory);

        _sectionStore = new Mock<IConfigSectionStore>();
        _sectionStore.Setup(x => x.GetSection<GatewayOptions>("Gateway")).Returns(new GatewayOptions());
        _sectionStore.Setup(x => x.GetSection<GatewayClientsOptions>("GatewayClients"))
            .Returns(new GatewayClientsOptions { Channels = new(StringComparer.OrdinalIgnoreCase) });

        var configStore = new ChannelHostConfigStore(_sectionStore.Object, _workspace.Object, registry);
        _managerLogger = new ListLogger<ChannelHostManager>();
        var manager = new ChannelHostManager(configStore, registry, _workspace.Object, _managerLogger);

        _gatewayServer = new Mock<IGatewayServer>();
        _serviceLogger = new ListLogger<ChannelHostHostedService>();
        var service = new ChannelHostHostedService(
            manager,
            configStore,
            _gatewayServer.Object,
            Options.Create(new GatewayOptions()),
            Mock.Of<IHostApplicationLifetime>(),
            _serviceLogger);

        _handler = new ChannelHostReloadHandler(service);
    }

    [Fact]
    public async Task ReloadAsync_包含GatewayClients节_启用通道未运行_应尝试启动()
    {
        // Arrange：网关已运行，wecom 通道启用且未运行
        _gatewayServer.SetupGet(x => x.IsRunning).Returns(true);
        SetChannels(("wecom", true));

        // Act
        await _handler.ReloadAsync(
            new ConfigChange { ChangedSections = new[] { "GatewayClients" } },
            CancellationToken.None);

        // Assert：StartAsync 被调度——主机存在且无通道配置文件 → 记录"跳过启动"；
        // 主机缺失 → 记录"热重载启动失败"（二者都证明启动流程被进入）
        (_managerLogger.Messages.Any(m => m.Contains("Channel 配置文件不存在，跳过启动"))
         || _serviceLogger.Messages.Any(m => m.Contains("热重载启动") && m.Contains("失败")))
            .Should().BeTrue();
    }

    [Fact]
    public async Task ReloadAsync_包含Gateway节_应触发协调()
    {
        // Arrange：网关已运行，wecom 通道启用且未运行
        _gatewayServer.SetupGet(x => x.IsRunning).Returns(true);
        SetChannels(("wecom", true));

        // Act：仅 "Gateway" 节变更（不含 GatewayClients）
        await _handler.ReloadAsync(
            new ConfigChange { ChangedSections = new[] { "Gateway" } },
            CancellationToken.None);

        // Assert：同上述，证明协调被触发
        (_managerLogger.Messages.Any(m => m.Contains("Channel 配置文件不存在，跳过启动"))
         || _serviceLogger.Messages.Any(m => m.Contains("热重载启动") && m.Contains("失败")))
            .Should().BeTrue();
    }

    [Fact]
    public async Task ReloadAsync_禁用通道未运行_不应启动()
    {
        // Arrange：网关已运行，wecom 通道禁用且未运行
        _gatewayServer.SetupGet(x => x.IsRunning).Returns(true);
        SetChannels(("wecom", false));

        // Act
        await _handler.ReloadAsync(
            new ConfigChange { ChangedSections = new[] { "GatewayClients" } },
            CancellationToken.None);

        // Assert：不产生任何启动迹象
        _managerLogger.Messages.Should().NotContain(m => m.Contains("跳过启动"));
        _serviceLogger.Messages.Should().NotContain(m => m.Contains("热重载启动"));
    }

    [Fact]
    public async Task ReloadAsync_空节列表_应全量触发协调()
    {
        // Arrange：网关未运行（协调入口会记录跳过日志）
        _gatewayServer.SetupGet(x => x.IsRunning).Returns(false);

        // Act
        await _handler.ReloadAsync(
            new ConfigChange { ChangedSections = Array.Empty<string>() },
            CancellationToken.None);

        // Assert：协调被触发（进入 ReconcileAsync 的网关未运行分支）
        _serviceLogger.Messages.Should().Contain(m => m.Contains("Gateway 尚未运行"));
    }

    [Fact]
    public async Task ReloadAsync_不相关配置节_不应触发协调()
    {
        // Arrange：网关未运行
        _gatewayServer.SetupGet(x => x.IsRunning).Returns(false);

        // Act
        await _handler.ReloadAsync(
            new ConfigChange { ChangedSections = new[] { "Other" } },
            CancellationToken.None);

        // Assert：未进入 ReconcileAsync
        _serviceLogger.Messages.Should().NotContain(m => m.Contains("Gateway 尚未运行"));
    }

    [Fact]
    public async Task ReloadAsync_非ConfigChange信号_应忽略()
    {
        // Arrange：网关未运行
        _gatewayServer.SetupGet(x => x.IsRunning).Returns(false);

        // Act
        await _handler.ReloadAsync(new WorkspaceChange(), CancellationToken.None);

        // Assert：未进入 ReconcileAsync
        _serviceLogger.Messages.Should().NotContain(m => m.Contains("Gateway 尚未运行"));
    }

    [Fact]
    public void Handler_应声明组件标识与订阅类型()
    {
        _handler.ComponentId.Should().Be("channel-host");
        _handler.ChangeTypes.Should().Contain(typeof(ConfigChange));
    }

    private void SetChannels(params (string ChannelId, bool Enabled)[] channels)
    {
        var entries = channels.ToDictionary(
            c => c.ChannelId,
            c => new GatewayChannelEntry { Enabled = c.Enabled },
            StringComparer.OrdinalIgnoreCase);
        _sectionStore.Setup(x => x.GetSection<GatewayClientsOptions>("GatewayClients"))
            .Returns(new GatewayClientsOptions { Channels = entries });
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
            Directory.Delete(_tempDirectory, recursive: true);
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
