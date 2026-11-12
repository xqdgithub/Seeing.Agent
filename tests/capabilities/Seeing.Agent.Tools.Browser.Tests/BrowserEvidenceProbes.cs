using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Tools.Browser;
using Xunit;

namespace Seeing.Agent.Tools.Browser.Tests;

/// <summary>
/// 证据探针：用真浏览器回答「补丁该不该留」，不是形态守门。
/// </summary>
/// <remarks>
/// 默认跳过；本机：<c>$env:SEEING_BROWSER_TESTS=1</c>。
/// </remarks>
[Trait("Category", "RequiresBrowser")]
public class BrowserEvidenceProbes
{
    private static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable("SEEING_BROWSER_TESTS"), "1", StringComparison.Ordinal);

    private const string PageA =
        "data:text/html,<title>A</title><body style='background:#fff'><h1 id=h>page-a</h1></body>";

    private const string PageB =
        "data:text/html,<title>B</title><body style='background:#eee'><h1 id=h>page-b</h1></body>";

    /// <summary>
    /// 导航后<b>不</b> Restart screencast，只要页面还有变化，是否仍能收到新帧？
    /// </summary>
    /// <remarks>
    /// 若本用例通过：端点里「Navigated → RestartAsync」是多余补丁，应删掉。
    /// 若失败：才有资格在采集层做导航后重建。
    /// </remarks>
    [Fact]
    public async Task Screencast_ShouldKeepDeliveringFramesAfterNavigation_WithoutRestart()
    {
        if (!Enabled)
            return;

        await using var provider = BuildProvider(out var options);
        var host = provider.GetRequiredService<IBrowserSessionHost>();
        var hub = new BrowserScreencastHub(options);

        var page = await host.GetPageAsync("probe_nav_frames", cancellationToken: TestContext.Current.CancellationToken);
        await page.NavigateAsync(PageA, BrowserWaitUntil.DomContentLoaded, TestContext.Current.CancellationToken);

        var sink = new CountingSink();
        await using var sub = await hub.SubscribeAsync(
            "probe_nav_frames", page, sink, TestContext.Current.CancellationToken);

        await PumpUntilAsync(page, () => sink.FrameCount > 0);
        var beforeNav = sink.FrameCount;
        beforeNav.Should().BeGreaterThan(0);

        // 关键：跨文档导航后不调用 hub.RestartAsync
        await page.NavigateAsync(PageB, BrowserWaitUntil.DomContentLoaded, TestContext.Current.CancellationToken);
        page.CurrentTitle.Should().Be("B");

        var target = beforeNav + 1;
        await PumpUntilAsync(page, () => sink.FrameCount >= target);

        sink.FrameCount.Should().BeGreaterThanOrEqualTo(
            target,
            "导航后不重启 screencast 仍应出新帧；若失败才说明需要 RestartAsync");

        await host.CloseAsync("probe_nav_frames", TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// 第二会话开页是否复用同一浏览器进程（不应二次 Launch）。
    /// </summary>
    [Fact]
    public async Task SecondSession_ShouldReuseSameBrowserProcess_NotRelaunch()
    {
        if (!Enabled)
            return;

        await using var provider = BuildProvider(out _);
        var host = provider.GetRequiredService<IBrowserSessionHost>();
        var engine = provider.GetRequiredService<BrowserEngine>();

        var a = await host.GetPageAsync("probe_sess_a", cancellationToken: TestContext.Current.CancellationToken);
        await a.NavigateAsync(PageA, BrowserWaitUntil.DomContentLoaded, TestContext.Current.CancellationToken);

        engine.IsStarted.Should().BeTrue();
        var pid = engine.GetType()
            .GetField("_browser", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            !.GetValue(engine);
        pid.Should().NotBeNull();
        var browser = (PuppeteerSharp.IBrowser)pid!;
        var processId = browser.Process?.Id;
        processId.Should().NotBeNull();

        var b = await host.GetPageAsync("probe_sess_b", cancellationToken: TestContext.Current.CancellationToken);
        await b.NavigateAsync(PageB, BrowserWaitUntil.DomContentLoaded, TestContext.Current.CancellationToken);

        engine.IsStarted.Should().BeTrue();
        var browser2 = (PuppeteerSharp.IBrowser)engine.GetType()
            .GetField("_browser", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            !.GetValue(engine)!;

        ReferenceEquals(browser, browser2).Should().BeTrue(
            "第二会话必须复用同一 IBrowser 实例；若换了实例，说明发生了二次 Launch");
        browser2.Process?.Id.Should().Be(processId,
            "进程 PID 变了 = 真的重新起了浏览器，切会话占用才可能来自这里");

        await host.CloseAsync("probe_sess_a", TestContext.Current.CancellationToken);
        await host.CloseAsync("probe_sess_b", TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// 主框架导航后 Navigated 是否发出、CurrentUrl/Title 是否更新（地址栏同步的真依据）。
    /// </summary>
    [Fact]
    public async Task MainFrameNavigation_ShouldRaiseNavigatedWithUpdatedIdentity()
    {
        if (!Enabled)
            return;

        await using var provider = BuildProvider(out _);
        var host = provider.GetRequiredService<IBrowserSessionHost>();

        var page = await host.GetPageAsync("probe_nav_event", cancellationToken: TestContext.Current.CancellationToken);
        await page.NavigateAsync(PageA, BrowserWaitUntil.DomContentLoaded, TestContext.Current.CancellationToken);

        var tcs = new TaskCompletionSource<BrowserPageHandle>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnNavigated(BrowserPageHandle p) => tcs.TrySetResult(p);
        page.Navigated += OnNavigated;

        try
        {
            await page.NavigateAsUserAsync(PageB, BrowserWaitUntil.DomContentLoaded, TestContext.Current.CancellationToken);

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            completed.Should().Be(tcs.Task, "主框架导航必须升起 Navigated，否则前端地址栏没有推送源");

            var raised = await tcs.Task;
            raised.Should().BeSameAs(page);
            raised.CurrentTitle.Should().Be("B");
        }
        finally
        {
            page.Navigated -= OnNavigated;
            await host.CloseAsync("probe_nav_event", TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// 点 <c>target="_blank"</c> 和 <c>window.open</c> 后，会话页必须自己打开目标，并且不留下新标签。
    /// </summary>
    /// <remarks>
    /// 真实事故：必应结果是新标签。有头窗口跟新标签，工作台仍订着原页；
    /// 原页若在后台跳走，两边就正好相反。无头没有窗口，但工作台同样不跟着新标签走。
    /// </remarks>
    [Fact]
    public async Task NewBrowsingContext_ShouldStayOnTheSessionPage()
    {
        if (!Enabled)
            return;

        // data: 页面再跳另一个 data: 会被浏览器拦住，测不出新标签。用本机 HTTP 页。
        using var site = new LoopbackSite();
        await using var provider = BuildProvider(out _, allowPrivateHosts: true);
        var host = provider.GetRequiredService<IBrowserSessionHost>();
        var ct = TestContext.Current.CancellationToken;
        var page = await host.GetPageAsync("probe_single_tab", cancellationToken: ct);

        var popups = 0;
        page.Page.Popup += (_, _) => Interlocked.Increment(ref popups);

        try
        {
            await page.NavigateAsync(site.OpenBlankUrl, BrowserWaitUntil.DomContentLoaded, ct);
            var scriptOn = await page.Page.EvaluateExpressionAsync<bool>(
                "(() => String(window.open).includes('assign'))()");
            var before = (await page.Page.Browser.PagesAsync()).Length;

            await page.ClickAsync(
                new BrowserElementTarget { Selector = "#go" },
                PuppeteerSharp.Input.MouseButton.Left,
                1,
                ct);

            try
            {
                await WaitUntilAsync(
                    () => page.CurrentTitle == "DEST",
                    null,
                    TimeSpan.FromSeconds(8),
                    TimeSpan.FromMilliseconds(20));
            }
            catch (TimeoutException)
            {
                throw new TimeoutException(
                    $"target=_blank 未折回。script={scriptOn} title={page.CurrentTitle} url={page.CurrentUrl} popups={popups} pages={await DescribePagesAsync(page)}");
            }

            var afterBlank = await DescribePagesAsync(page);
            page.CurrentTitle.Should().Be(
                "DEST",
                "target=_blank 必须在当前页打开。script={0} 实际：{1} popups={2}",
                scriptOn, afterBlank, popups);
            (await page.Page.Browser.PagesAsync()).Length.Should().Be(
                before,
                "新标签必须被折回或根本不创建。实际：{0} popups={1}", afterBlank, popups);

            await page.NavigateAsync(site.OpenWinUrl, BrowserWaitUntil.DomContentLoaded, ct);
            var beforeOpen = (await page.Page.Browser.PagesAsync()).Length;

            await page.ClickAsync(
                new BrowserElementTarget { Selector = "#win" },
                PuppeteerSharp.Input.MouseButton.Left,
                1,
                ct);

            try
            {
                await WaitUntilAsync(
                    () => page.CurrentTitle == "WIN",
                    null,
                    TimeSpan.FromSeconds(8),
                    TimeSpan.FromMilliseconds(20));
            }
            catch (TimeoutException)
            {
                throw new TimeoutException(
                    $"window.open 未折回。title={page.CurrentTitle} url={page.CurrentUrl} popups={popups} pages={await DescribePagesAsync(page)}");
            }

            var afterOpen = await DescribePagesAsync(page);
            page.CurrentTitle.Should().Be("WIN", "window.open 必须在当前页打开。实际：{0} popups={1}", afterOpen, popups);
            (await page.Page.Browser.PagesAsync()).Length.Should().Be(
                beforeOpen,
                "window.open 不得留下新标签。实际：{0} popups={1}", afterOpen, popups);
        }
        finally
        {
            await host.CloseAsync("probe_single_tab", ct);
        }
    }

    /// <summary>只回答 GET 的本机站点，用来制造 target=_blank / window.open。</summary>
    private sealed class LoopbackSite : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();

        public LoopbackSite()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptAsync(_stop.Token);
        }

        public int Port { get; }

        public string OpenBlankUrl => $"http://127.0.0.1:{Port}/open";

        public string OpenWinUrl => $"http://127.0.0.1:{Port}/open-win";

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }

        private async Task AcceptAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    return;
                }

                _ = Task.Run(() => WriteAsync(client), CancellationToken.None);
            }
        }

        private static async Task WriteAsync(TcpClient client)
        {
            using (client)
            using (var stream = client.GetStream())
            {
                var buffer = new byte[2048];
                var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
                var request = Encoding.ASCII.GetString(buffer, 0, read);
                var path = request.Split(' ', 3) is { Length: >= 2 } parts ? parts[1] : "/";
                var query = path.IndexOf('?', StringComparison.Ordinal);
                if (query >= 0)
                    path = path[..query];

                var body = path switch
                {
                    "/dest" => "<title>DEST</title><h1>dest-page</h1>",
                    "/win" => "<title>WIN</title><h1>win-page</h1>",
                    "/open-win" => "<title>OPEN2</title><button id=\"win\" onclick=\"window.open('/win')\">w</button>",
                    "/open" => "<title>OPEN</title><a id=\"go\" target=\"_blank\" href=\"/dest\">go</a>",
                    _ => ""
                };

                var status = body.Length == 0 ? "404 Not Found" : "200 OK";
                var payload = Encoding.UTF8.GetBytes(body);
                var header = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header).ConfigureAwait(false);
                await stream.WriteAsync(payload).ConfigureAwait(false);
            }
        }
    }

    private static async Task<string> DescribePagesAsync(BrowserPageHandle page)
    {
        var pages = await page.Page.Browser.PagesAsync();
        var parts = new List<string>();
        foreach (var item in pages)
        {
            string title;
            try
            {
                title = await item.GetTitleAsync();
            }
            catch
            {
                title = "?";
            }

            parts.Add($"{title}@{item.Url}");
        }

        return string.Join(" || ", parts);
    }

    private static ServiceProvider BuildProvider(
        out TestOptionsMonitor<BrowserOptions> options,
        bool allowPrivateHosts = false)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        options = new TestOptionsMonitor<BrowserOptions>(new BrowserOptions
        {
            Headless = true,
            IdleSeconds = 0,
            PersistLogin = false,
            AllowPrivateHosts = allowPrivateHosts,
            ViewportWidth = 640,
            ViewportHeight = 480
        });
        services.AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<BrowserOptions>>(options);
        services.AddSingleton<BrowserEngine>();
        services.AddSingleton<BrowserSessionHost>();
        services.AddSingleton<IBrowserSessionHost>(sp => sp.GetRequiredService<BrowserSessionHost>());
        return services.BuildServiceProvider();
    }

    private static Task PumpUntilAsync(BrowserPageHandle page, Func<bool> condition)
        => WaitUntilAsync(
            condition,
            async i =>
            {
                await page.EvaluateAsync(
                    $"document.body.style.outline = '{i % 2}px solid red'",
                    true,
                    TestContext.Current.CancellationToken).ConfigureAwait(false);
            },
            timeout: TimeSpan.FromSeconds(5),
            pollInterval: TimeSpan.FromMilliseconds(20));

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

    private sealed class CountingSink : IBrowserFrameSink
    {
        private int _frames;
        public int FrameCount => Volatile.Read(ref _frames);
        public void OnFrame(BrowserFrame frame) => Interlocked.Increment(ref _frames);
        public void OnState(BrowserPageState state) { }
        public void OnError(string message) { }
    }
}
