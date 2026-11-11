using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Seeing.Agent.Tools.Browser;
using Xunit;

namespace Seeing.Agent.Tools.Browser.Tests;

/// <summary>
/// 页面创建通知总线：agent 用工具建页面后，画面流必须被唤醒。
/// </summary>
/// <remarks>
/// <para>
/// <b>这是「浏览器一直黑屏、用工具操作没反应」的根因守门。</b>
/// 页面可以由 agent 的工具（<c>browser_open</c>）创建，而画面流订阅由 WS 端点建立；
/// 端点只在「连接建立」与「收到用户输入」时检查页面是否存在 —— agent 那条路径两个时机都不触发。
/// 缺了通知总线，工具明明在操作页面、画布却永远停在「浏览器尚未启动」。
/// </para>
/// <para>
/// 这里验证总线的登记/广播/隔离/释放契约；端到端链路（真建页面 → 收到通知）由
/// <c>BrowserEndToEndTests.AgentCreatedPage_ShouldReachListener</c> 覆盖。
/// </para>
/// </remarks>
public class BrowserPageNotifierTests
{
    private static (ServiceProvider Provider, BrowserPageNotifier Notifier) Create()
    {
        var services = new ServiceCollection();
        var options = new TestOptionsMonitor<BrowserOptions>(new BrowserOptions { Headless = true, IdleSeconds = 0 });

        services.AddSingleton<IOptionsMonitor<BrowserOptions>>(options);
        services.AddSingleton<BrowserEngine>();
        services.AddSingleton<BrowserSessionHost>();
        services.AddSingleton<BrowserPageNotifier>();

        var provider = services.BuildServiceProvider();
        return (provider, provider.GetRequiredService<BrowserPageNotifier>());
    }

    /// <summary>总线的广播入口（页面创建时由宿主调用）。</summary>
    private static Task Broadcast(BrowserPageNotifier notifier, string sessionId, BrowserPageHandle? page)
        => notifier.PublishForTestAsync(sessionId, page);

    [Fact]
    public async Task Listen_ShouldReceiveBroadcast()
    {
        var (provider, notifier) = Create();
        await using var _ = provider;

        await notifier.StartAsync(TestContext.Current.CancellationToken);

        var received = false;
        using var listener = notifier.Listen("s1", _ =>
        {
            received = true;
            return Task.CompletedTask;
        });

        await Broadcast(notifier, "s1", null);

        received.Should().BeTrue("页面创建通知必须送到订阅者，否则画布永远停在未启动态");
    }

    [Fact]
    public async Task Listen_OtherSession_ShouldNotReceiveBroadcast()
    {
        var (provider, notifier) = Create();
        await using var _ = provider;

        await notifier.StartAsync(TestContext.Current.CancellationToken);

        var received = false;
        using var listener = notifier.Listen("s_other", _ =>
        {
            received = true;
            return Task.CompletedTask;
        });

        await Broadcast(notifier, "s1", null);

        received.Should().BeFalse("通知按会话隔离，不能串到别的会话");
    }

    [Fact]
    public async Task DisposeListener_ShouldStopReceiving()
    {
        var (provider, notifier) = Create();
        await using var _ = provider;

        await notifier.StartAsync(TestContext.Current.CancellationToken);

        var count = 0;
        var listener = notifier.Listen("s1", _ =>
        {
            Interlocked.Increment(ref count);
            return Task.CompletedTask;
        });

        await Broadcast(notifier, "s1", null);
        count.Should().Be(1);

        listener.Dispose();
        await Broadcast(notifier, "s1", null);
        count.Should().Be(1, "释放后不应再收到通知");
    }

    [Fact]
    public async Task MultipleListeners_ShouldAllReceive()
    {
        var (provider, notifier) = Create();
        await using var _ = provider;

        await notifier.StartAsync(TestContext.Current.CancellationToken);

        var count = 0;
        using var a = notifier.Listen("s1", _ => { Interlocked.Increment(ref count); return Task.CompletedTask; });
        using var b = notifier.Listen("s1", _ => { Interlocked.Increment(ref count); return Task.CompletedTask; });

        await Broadcast(notifier, "s1", null);

        count.Should().Be(2, "多标签页同时看同一会话时两个观看者都要被唤醒");
    }

    [Fact]
    public async Task StartStop_ShouldBeIdempotent()
    {
        var (provider, notifier) = Create();
        await using var _ = provider;

        notifier.IsRunning.Should().BeFalse();

        await notifier.StartAsync(TestContext.Current.CancellationToken);
        await notifier.StartAsync(TestContext.Current.CancellationToken);
        notifier.IsRunning.Should().BeTrue();

        await notifier.StopAsync(TestContext.Current.CancellationToken);
        await notifier.StopAsync(TestContext.Current.CancellationToken);
        notifier.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task ModuleId_ShouldMatchBrowserModule()
    {
        var (provider, notifier) = Create();
        await using var _ = provider;

        notifier.ModuleId.Should().Be(new BrowserModule().Id);
    }
}
