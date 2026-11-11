using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Sessions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Core.Output;
using Seeing.Agent.Tools.Browser;
using Seeing.Agent.Tools.Browser.Tools;
using Seeing.Session.Storage;
using Xunit;

namespace Seeing.Agent.Tools.Browser.Tests;

/// <summary>
/// 真实浏览器端到端：启动 headless Edge → 打开页面 → 快照 → 按 ref 点击 → 输入 → 截图 → screencast → 关闭。
/// </summary>
/// <remarks>
/// <para>
/// <b>默认跳过</b>：CI 环境未必装了浏览器，也不该让单元测试套件依赖它。本机跑：
/// <c>set SEEING_BROWSER_TESTS=1</c> 后再执行。
/// </para>
/// <para>
/// 它不是「集成测试的替代品」—— 单测覆盖策略与渲染，这条用例只担保「真的能开起来并点得动」。
/// </para>
/// </remarks>
[Trait("Category", "RequiresBrowser")]
public class BrowserEndToEndTests
{
    private static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable("SEEING_BROWSER_TESTS"), "1", StringComparison.Ordinal);

    private const string Page =
        "data:text/html,<title>Probe</title><h1>Probe</h1>" +
        "<input id=q placeholder='search'>" +
        "<button id=b onclick=\"document.getElementById('out').textContent='clicked'\">Go</button>" +
        "<div id=out>idle</div>" +
        "<a href='#x'>link</a>";

    [Fact]
    public async Task FullJourney_ShouldWorkAgainstRealBrowser()
    {
        if (!Enabled)
            return;   // 未开开关：视为通过（见类型注释）

        var tempDir = Path.Combine(Path.GetTempPath(), "seeing-browser-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var services = new ServiceCollection();
            services.AddLogging();

            var options = new TestOptionsMonitor<BrowserOptions>(new BrowserOptions
            {
                Headless = true,
                IdleSeconds = 0,
                ViewportWidth = 900,
                ViewportHeight = 600
            });

            services.AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<BrowserOptions>>(options);
            services.AddSingleton<BrowserEngine>();
            services.AddSingleton<BrowserSessionHost>();
            services.AddSingleton<IBrowserSessionHost>(sp => sp.GetRequiredService<BrowserSessionHost>());
            services.AddSingleton<Seeing.Agent.Core.Output.IToolOutputStore>(sp =>
                new SessionToolOutputStore(new FileSessionStore(tempDir)));
            services.AddSingleton<ISessionArtifactStore>(sp =>
                new SessionArtifactStore(sp.GetRequiredService<Seeing.Agent.Core.Output.IToolOutputStore>()));

            await using var provider = services.BuildServiceProvider();

            var host = provider.GetRequiredService<IBrowserSessionHost>();
            var artifacts = provider.GetRequiredService<ISessionArtifactStore>();
            const string sessionId = "s_e2e";

            // 1) 打开页面
            var open = new BrowserOpenTool(NullLogger<BrowserOpenTool>.Instance, host, options);
            var openResult = await open.ExecuteAsync(
                JsonDocument.Parse(JsonSerializer.Serialize(new { url = Page })).RootElement,
                new ToolContext { SessionId = sessionId });
            openResult.Success.Should().BeTrue(openResult.Error);

            // 2) 快照：必须拿到 ref
            var snapshot = new BrowserSnapshotTool(NullLogger<BrowserSnapshotTool>.Instance, host);
            var snapshotResult = await snapshot.ExecuteAsync(
                JsonDocument.Parse("""{"includeText":true}""").RootElement,
                new ToolContext { SessionId = sessionId });
            snapshotResult.Success.Should().BeTrue(snapshotResult.Error);
            snapshotResult.Output.Should().Contain("Probe");

            var buttonRef = FindRef(snapshotResult.Output, "button", "Go");
            buttonRef.Should().NotBeNull("快照必须为按钮生成 ref");

            var inputRef = FindRef(snapshotResult.Output, "textbox", "search");
            inputRef.Should().NotBeNull("快照必须为输入框生成 ref");

            // 3) 输入文字
            var type = new BrowserTypeTool(NullLogger<BrowserTypeTool>.Instance, host);
            var typeResult = await type.ExecuteAsync(
                JsonDocument.Parse(JsonSerializer.Serialize(new { @ref = inputRef, text = "hello" })).RootElement,
                new ToolContext { SessionId = sessionId });
            typeResult.Success.Should().BeTrue(typeResult.Error);

            // 4) 按 ref 点击按钮
            var click = new BrowserClickTool(NullLogger<BrowserClickTool>.Instance, host);
            var clickResult = await click.ExecuteAsync(
                JsonDocument.Parse(JsonSerializer.Serialize(new { @ref = buttonRef })).RootElement,
                new ToolContext { SessionId = sessionId });
            clickResult.Success.Should().BeTrue(clickResult.Error);

            // 5) 用 eval 验证点击真的生效（DOM 事实）
            var eval = new BrowserEvalTool(NullLogger<BrowserEvalTool>.Instance, host);
            var evalResult = await eval.ExecuteAsync(
                JsonDocument.Parse("""{"expression":"document.getElementById('out').textContent"}""").RootElement,
                new ToolContext { SessionId = sessionId });
            evalResult.Success.Should().BeTrue(evalResult.Error);
            evalResult.Output.Should().Contain("clicked", "点击必须真的改变了页面状态");

            // 6) 截图：必须有图片内容段，且落在会话资源目录
            var screenshot = new BrowserScreenshotTool(NullLogger<BrowserScreenshotTool>.Instance, host, artifacts);
            var shotResult = await screenshot.ExecuteAsync(
                JsonDocument.Parse("{}").RootElement,
                new ToolContext { SessionId = sessionId });
            shotResult.Success.Should().BeTrue(shotResult.Error);
            shotResult.Parts.Should().NotBeNull();
            shotResult.Parts!.Should().ContainSingle();
            shotResult.Parts[0].Kind.Should().Be(ToolOutputPartKind.Image);
            File.Exists(shotResult.Parts[0].LocalPath).Should().BeTrue("截图必须真的落盘");
            new FileInfo(shotResult.Parts[0].LocalPath!).Length.Should().BeGreaterThan(1000);

            // 7) 关闭
            var close = new BrowserCloseTool(NullLogger<BrowserCloseTool>.Instance, host);
            var closeResult = await close.ExecuteAsync(
                JsonDocument.Parse("{}").RootElement,
                new ToolContext { SessionId = sessionId });
            closeResult.Success.Should().BeTrue(closeResult.Error);
            host.HasPage(sessionId).Should().BeFalse();
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // 清理失败不影响断言
            }
        }
    }

    [Fact]
    public async Task Screencast_ShouldDeliverFrames()
    {
        if (!Enabled)
            return;

        var services = new ServiceCollection();
        services.AddLogging();

        var options = new TestOptionsMonitor<BrowserOptions>(new BrowserOptions
        {
            Headless = true,
            IdleSeconds = 0,
            ViewportWidth = 640,
            ViewportHeight = 480
        });

        services.AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<BrowserOptions>>(options);
        services.AddSingleton<BrowserEngine>();
        services.AddSingleton<BrowserSessionHost>();
        services.AddSingleton<IBrowserSessionHost>(sp => sp.GetRequiredService<BrowserSessionHost>());

        await using var provider = services.BuildServiceProvider();

        var host = provider.GetRequiredService<IBrowserSessionHost>();
        var hub = new BrowserScreencastHub(options);

        var page = await host.GetPageAsync("s_frames", cancellationToken: TestContext.Current.CancellationToken);
        await page.NavigateAsync(Page, BrowserWaitUntil.DomContentLoaded, TestContext.Current.CancellationToken);

        var sink = new CountingSink();
        await using var subscription = await hub.SubscribeAsync(
            "s_frames", page, sink, TestContext.Current.CancellationToken);

        // 触发画面变化以推出帧（无变化时 Chromium 不推）。
        // 用条件轮询而不是「循环 8 次 × 120ms」：后者是靠 sleep 猜窗口，慢机器上会随机失败。
        await PumpFramesAsync(page, sink);

        sink.FrameCount.Should().BeGreaterThan(0, "screencast 必须真的推出帧（含 ack 往返）");
        sink.LastFrame!.Data.Length.Should().BeGreaterThan(0);

        await host.CloseAsync("s_frames", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CloseThenReopen_ScreencastShouldStillDeliverFrames()
    {
        if (!Enabled)
            return;

        var services = new ServiceCollection();
        services.AddLogging();

        var options = new TestOptionsMonitor<BrowserOptions>(new BrowserOptions
        {
            Headless = true,
            IdleSeconds = 0,
            ViewportWidth = 640,
            ViewportHeight = 480
        });

        services.AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<BrowserOptions>>(options);
        services.AddSingleton<BrowserEngine>();
        services.AddSingleton<BrowserSessionHost>();
        services.AddSingleton<IBrowserSessionHost>(sp => sp.GetRequiredService<BrowserSessionHost>());

        await using var provider = services.BuildServiceProvider();

        var host = provider.GetRequiredService<BrowserSessionHost>();
        var hub = new BrowserScreencastHub(options);
        var lifetime = new BrowserLifetimeService(host, hub);
        await lifetime.StartAsync(TestContext.Current.CancellationToken);

        const string sessionId = "s_reopen";

        // 第一轮：开页面 + 收帧
        var first = await host.GetPageAsync(sessionId, cancellationToken: TestContext.Current.CancellationToken);
        await first.NavigateAsync(Page, BrowserWaitUntil.DomContentLoaded, TestContext.Current.CancellationToken);

        var firstSink = new CountingSink();
        var firstSub = await hub.SubscribeAsync(sessionId, first, firstSink, TestContext.Current.CancellationToken);
        await PumpFramesAsync(first, firstSink);
        firstSink.FrameCount.Should().BeGreaterThan(0);
        await firstSub.DisposeAsync();

        // 关闭：必须联动释放画面流（否则重开会复用指向已关闭页面的 CDP 会话）
        await host.CloseAsync(sessionId, TestContext.Current.CancellationToken);
        host.HasPage(sessionId).Should().BeFalse();

        // 第二轮：重开同一会话，画面必须仍能推出帧
        var second = await host.GetPageAsync(sessionId, cancellationToken: TestContext.Current.CancellationToken);
        await second.NavigateAsync(Page, BrowserWaitUntil.DomContentLoaded, TestContext.Current.CancellationToken);

        var secondSink = new CountingSink();
        await using var secondSub = await hub.SubscribeAsync(
            sessionId, second, secondSink, TestContext.Current.CancellationToken);

        await PumpFramesAsync(second, secondSink);

        secondSink.FrameCount.Should().BeGreaterThan(
            0, "关闭后重开必须重新建立 screencast；否则画面永远停在最后一帧");

        await host.CloseAsync(sessionId, TestContext.Current.CancellationToken);
        await lifetime.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// 页面身份守门：<c>PageClosed</c> 必须带上被关闭的页面实例，
    /// 且 <c>DropSessionAsync</c> 只清「指向它」的那条流。
    /// </summary>
    /// <remarks>
    /// 根因场景：close 与 reopen 交错 —— 慢速关闭的完成事件在新页面已订阅之后才到。
    /// 若事件只带 sessionId，新页面的健康流会被删掉（表现为「刚打开就黑屏、再无帧」）。
    /// 这里用真实浏览器验证「关闭 → 立即重开 → 仍能出帧」。
    /// </remarks>
    [Fact]
    public async Task CloseThenImmediatelyReopen_ShouldNotKillNewStream()
    {
        if (!Enabled)
            return;

        var services = new ServiceCollection();
        services.AddLogging();

        var options = new TestOptionsMonitor<BrowserOptions>(new BrowserOptions
        {
            Headless = true,
            IdleSeconds = 0,
            ViewportWidth = 640,
            ViewportHeight = 480
        });

        services.AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<BrowserOptions>>(options);
        services.AddSingleton<BrowserEngine>();
        services.AddSingleton<BrowserSessionHost>();
        services.AddSingleton<IBrowserSessionHost>(sp => sp.GetRequiredService<BrowserSessionHost>());

        await using var provider = services.BuildServiceProvider();

        var host = provider.GetRequiredService<BrowserSessionHost>();
        var hub = new BrowserScreencastHub(options);
        var lifetime = new BrowserLifetimeService(host, hub);
        await lifetime.StartAsync(TestContext.Current.CancellationToken);

        const string sessionId = "s_racy_reopen";
        var closedPages = new List<BrowserPageHandle>();

        host.PageClosed += (_, page) =>
        {
            lock (closedPages)
                closedPages.Add(page);

            return Task.CompletedTask;
        };

        // 先建页面并订阅（模拟已有人看着）
        var first = await host.GetPageAsync(sessionId, cancellationToken: TestContext.Current.CancellationToken);
        var sink = new CountingSink();
        var firstSub = await hub.SubscribeAsync(sessionId, first, sink, TestContext.Current.CancellationToken);
        await PumpFramesAsync(first, sink);

        // 关闭后立刻重开（模拟「关掉再打开」连点）
        await host.CloseAsync(sessionId, TestContext.Current.CancellationToken);

        var second = await host.GetPageAsync(sessionId, cancellationToken: TestContext.Current.CancellationToken);
        second.Should().NotBeSameAs(first, "重开必须产生新页面实例");

        var secondSink = new CountingSink();
        await using var secondSub = await hub.SubscribeAsync(
            sessionId, second, secondSink, TestContext.Current.CancellationToken);

        await PumpFramesAsync(second, secondSink);

        // 关键断言：新页面的流仍活着（若 DropSession 按 sessionId 误杀，这里会是 0）
        secondSink.FrameCount.Should().BeGreaterThan(
            0, "关闭旧页面不能误杀新页面的画面流");

        // 事件必须带页面实例（端点据此判定是否需要重订阅）
        closedPages.Should().Contain(first, "PageClosed 必须携带被关闭的那个页面实例");

        await firstSub.DisposeAsync();
        await host.CloseAsync(sessionId, TestContext.Current.CancellationToken);
        await lifetime.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// 根因守门：「agent 用工具建页面」必须能唤醒画面流监听者。
    /// </summary>
    /// <remarks>
    /// 用户报的症状是「浏览器一直黑屏、用工具操作没反应」。
    /// 根因：页面由 agent 的工具创建，而画面流订阅由 WS 端点建立；端点只在
    /// 「连接建立」与「收到用户输入」时检查页面，agent 那条路径两个时机都不触发。
    /// 本用例走真实浏览器，验证 <c>PageCreated</c> 能把通知送到监听者（端点的订阅入口）。
    /// </remarks>
    [Fact]
    public async Task AgentCreatedPage_ShouldReachListener()
    {
        if (!Enabled)
            return;

        var services = new ServiceCollection();
        services.AddLogging();

        var options = new TestOptionsMonitor<BrowserOptions>(new BrowserOptions
        {
            Headless = true,
            IdleSeconds = 0,
            ViewportWidth = 640,
            ViewportHeight = 480
        });

        services.AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<BrowserOptions>>(options);
        services.AddSingleton<BrowserEngine>();
        services.AddSingleton<BrowserSessionHost>();
        services.AddSingleton<BrowserPageNotifier>();

        await using var provider = services.BuildServiceProvider();

        var host = provider.GetRequiredService<BrowserSessionHost>();
        var notifier = provider.GetRequiredService<BrowserPageNotifier>();
        await notifier.StartAsync(TestContext.Current.CancellationToken);

        const string sessionId = "s_agent_created";
        var notified = new TaskCompletionSource<BrowserPageHandle>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var listener = notifier.Listen(sessionId, page =>
        {
            notified.TrySetResult(page);
            return Task.CompletedTask;
        });

        // 等价于 agent 调 browser_open：直接让宿主创建页面（端点此时毫不知情）
        var created = await host.GetPageAsync(sessionId, cancellationToken: TestContext.Current.CancellationToken);

        var got = await Task.WhenAny(notified.Task, Task.Delay(TimeSpan.FromSeconds(5)))
            == notified.Task;

        got.Should().BeTrue("agent 建页面必须通知监听者，否则画布永远停在「浏览器尚未启动」");
        (await notified.Task).Should().BeSameAs(created);

        await host.CloseAsync(sessionId, TestContext.Current.CancellationToken);
        await notifier.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// 触发画面变化直到收到首帧。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么不能只靠「改几次样式 + 每次等 120ms」</b>：那是靠 sleep 猜窗口——
    /// 慢机器上首帧可能 500ms 后才到，用例随机失败；快机器上白等几秒。
    /// </para>
    /// <para>
    /// <b>当前做法的取舍</b>：Chromium 的 screencast 只在「画面有变化」时出帧，
    /// 因此没有「首帧到达」这个可 await 的完成信号可用（帧是被动的 CDP 推送）。
    /// 这里改为<b>短间隔条件轮询</b>：每 20ms 制造一次变化并检查是否已收到帧，
    /// 命中即返回（属 AGENTS.md 例外表 E5「轮询间隔」），总上限仍是 4 秒。
    /// </para>
    /// </remarks>
    private static Task PumpFramesAsync(BrowserPageHandle page, CountingSink sink)
        => WaitUntilAsync(
            () => sink.FrameCount > 0,
            async i =>
            {
                // 每次轮询都制造一次可见变化：否则 Chromium 不会推送新帧
                await page.EvaluateAsync(
                    $"document.body.style.background = '#{i % 2}0{i % 2}0{i % 2}0'",
                    true,
                    TestContext.Current.CancellationToken).ConfigureAwait(false);
            },
            timeout: TimeSpan.FromSeconds(4),
            pollInterval: TimeSpan.FromMilliseconds(20));

    /// <summary>
    /// 条件轮询：命中即返回，超时抛 <see cref="TimeoutException"/>。
    /// </summary>
    /// <param name="condition">判定条件。</param>
    /// <param name="onTick">每次轮询前的动作（制造触发条件用）。</param>
    /// <param name="timeout">总上限。</param>
    /// <param name="pollInterval">轮询间隔（E5 例外：命中即返回，不是固定等待）。</param>
    private static async Task WaitUntilAsync(
        Func<bool> condition,
        Func<int, Task>? onTick,
        TimeSpan timeout,
        TimeSpan pollInterval)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        for (var i = 0; DateTimeOffset.UtcNow < deadline; i++)
        {
            if (condition())
                return;

            if (onTick is not null)
                await onTick(i).ConfigureAwait(false);

            if (condition())
                return;

            await Task.Delay(pollInterval, TestContext.Current.CancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"条件在 {timeout.TotalSeconds:F0} 秒内未成立");
    }

    private static string? FindRef(string snapshotText, string role, string nameFragment)
    {
        foreach (var line in snapshotText.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith('e'))
                continue;

            if (trimmed.Contains(role, StringComparison.Ordinal)
                && trimmed.Contains(nameFragment, StringComparison.Ordinal))
            {
                var space = trimmed.IndexOf(' ');
                if (space > 0)
                    return trimmed[..space];
            }
        }

        return null;
    }

    private sealed class CountingSink : IBrowserFrameSink
    {
        private int _frames;

        public int FrameCount => Volatile.Read(ref _frames);

        public BrowserFrame? LastFrame { get; private set; }

        public void OnFrame(BrowserFrame frame)
        {
            LastFrame = frame;
            Interlocked.Increment(ref _frames);
        }

        public void OnState(BrowserPageState state)
        {
        }

        public void OnError(string message)
        {
        }
    }
}
