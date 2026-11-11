using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Seeing.Agent.Tools.Browser;
using Xunit;

namespace Seeing.Agent.Tools.Browser.Tests;

/// <summary>
/// 生命周期守门：模块停用后再启用必须仍可用。
/// <para>
/// 历史缺陷：`DeactivateAsync` 直接 `DisposeAsync()` DI 单例，内部信号量被释放；
/// 热重载重新启用后调用抛 <c>ObjectDisposedException</c>，表现为「停用再启用后浏览器永远起不来」。
/// 这里钉住「停用 → 启用 → 再次关停」不抛。
/// </para>
/// </summary>
public class BrowserLifetimeTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        var options = new TestOptionsMonitor<BrowserOptions>(new BrowserOptions { Headless = true, IdleSeconds = 0 });

        services.AddSingleton<IOptionsMonitor<BrowserOptions>>(options);
        services.AddSingleton<BrowserEngine>();
        services.AddSingleton<BrowserSessionHost>();
        services.AddSingleton<IBrowserSessionHost>(sp => sp.GetRequiredService<BrowserSessionHost>());
        services.AddSingleton<BrowserScreencastHub>();
        services.AddSingleton<BrowserLifetimeService>();

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task ShutdownThenReuse_ShouldNotThrowOnDisposedSemaphore()
    {
        await using var provider = BuildProvider();

        var engine = provider.GetRequiredService<BrowserEngine>();
        var host = provider.GetRequiredService<BrowserSessionHost>();
        var hub = provider.GetRequiredService<BrowserScreencastHub>();

        // 停用（模块 Deactivate 的实际调用序列）
        await hub.DisposeAsync();
        await host.ShutdownAsync(TestContext.Current.CancellationToken);
        await engine.ShutdownAsync(TestContext.Current.CancellationToken);

        // 重新启用后再次关停：不得因「信号量已释放」抛 ObjectDisposedException
        await hub.DisposeAsync();
        await host.ShutdownAsync(TestContext.Current.CancellationToken);
        await engine.ShutdownAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Shutdown_ShouldLeaveHostUsable()
    {
        await using var provider = BuildProvider();

        var host = provider.GetRequiredService<BrowserSessionHost>();
        await host.ShutdownAsync(TestContext.Current.CancellationToken);

        // 关停后查询不应抛（内部状态仍可读，只是没有页面）
        host.HasPage("s_any").Should().BeFalse();
        host.TryGetPage("s_any").Should().BeNull();
        host.OpenSessionCount.Should().Be(0);
    }

    [Fact]
    public async Task LifetimeService_StartStop_ShouldBeIdempotent()
    {
        await using var provider = BuildProvider();
        var lifetime = provider.GetRequiredService<BrowserLifetimeService>();

        lifetime.IsRunning.Should().BeFalse();

        await lifetime.StartAsync(TestContext.Current.CancellationToken);
        await lifetime.StartAsync(TestContext.Current.CancellationToken);
        lifetime.IsRunning.Should().BeTrue();

        await lifetime.StopAsync(TestContext.Current.CancellationToken);
        await lifetime.StopAsync(TestContext.Current.CancellationToken);
        lifetime.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task PageClosedEvent_ShouldFireOnClose()
    {
        await using var provider = BuildProvider();

        var host = provider.GetRequiredService<BrowserSessionHost>();
        var closed = new List<string>();

        host.PageClosed += (sessionId, _) =>
        {
            closed.Add(sessionId);
            return Task.CompletedTask;
        };

        // 没有页面时关闭：不应触发（没有东西被关掉）
        await host.CloseAsync("s_none", TestContext.Current.CancellationToken);
        closed.Should().BeEmpty();

        // 制造一个「已有页面」的假状态：直接调用内部关闭路径不可行，
        // 因此这里验证的是「事件契约存在且无页面时不误报」这一半；
        // 真正的联动由 BrowserEndToEndTests（真浏览器）覆盖。
        host.OpenSessionCount.Should().Be(0);
    }

    [Fact]
    public void LifetimeService_ShouldBelongToBrowserModule()
    {
        new BrowserLifetimeService(
            new BrowserSessionHost(
                new BrowserEngine(new TestOptionsMonitor<BrowserOptions>(new BrowserOptions())),
                new TestOptionsMonitor<BrowserOptions>(new BrowserOptions()),
                NullLogger<BrowserSessionHost>.Instance),
            new BrowserScreencastHub(new TestOptionsMonitor<BrowserOptions>(new BrowserOptions())),
            NullLogger<BrowserLifetimeService>.Instance)
            .ModuleId.Should().Be("browser");
    }
}
