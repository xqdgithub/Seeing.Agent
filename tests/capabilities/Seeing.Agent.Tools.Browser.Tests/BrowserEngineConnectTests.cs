using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PuppeteerSharp;
using Seeing.Agent.Tools.Browser;
using Xunit;

namespace Seeing.Agent.Tools.Browser.Tests;

/// <summary>
/// 客户端连接 / 断开守门：连上共享宿主、**断开不关浏览器**、未建页时不占用宿主。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么断开方式必须被钉住</b>：<c>IBrowser</c> 没有 <c>DisconnectAsync</c>，只有同步
/// <c>Disconnect()</c>；而 <c>CloseAsync</c> / <c>DisposeAsync</c> 会向共享浏览器发
/// <c>Browser.close</c> —— 一个客户端退出会杀掉所有人的 Chromium（含别人的页面与登录态）。
/// </para>
/// <para>
/// <b>为什么「未建页不产生锁」也要钉住</b>：宿主靠客户端存活锁判断能否空闲退出。
/// 「UI 常开但从没用过浏览器」若提前持锁，宿主永远不休眠（Ruling 6：presence 懒创建）。
/// </para>
/// <para>
/// 真实浏览器用例需要 <c>SEEING_BROWSER_TESTS=1</c>（见 <see cref="BrowserTestGate"/>）。
/// </para>
/// </remarks>
[Trait("Category", "RequiresBrowser")]
public class BrowserEngineConnectTests
{
    [Fact]
    public async Task Shutdown_ShouldNotCloseSharedBrowser()
    {
        if (!BrowserTestGate.Enabled)
            Assert.Skip("需要真实浏览器：设置 SEEING_BROWSER_TESTS=1 后运行");

        if (!BrowserEngine.ResolveExecutable(new BrowserOptions()).Found)
            Assert.Skip("本机没有可用 Chromium / Edge，跳过真实连接用例");

        var ct = TestContext.Current.CancellationToken;
        var profile = NewProfile();
        Directory.CreateDirectory(profile);

        IBrowser? second = null;
        try
        {
            // 用 Cli 起宿主（A4 约定：测试优先走 Cli，避免依赖 Gateway 的端口/分发）。
            TestHost.Start(profile);

            var options = Options(profile);
            var engine = new BrowserEngine(options, logger: null, baseDirectoryOverride: profile);

            await engine.GetBrowserAsync(ct);
            engine.IsStarted.Should().BeTrue();

            var endpoint = engine.CurrentEndpointForTest;
            endpoint.Should().NotBeNullOrWhiteSpace("连接后必须记录实际 endpoint，供诊断与测试复核");

            await engine.ShutdownAsync(ct);
            engine.IsStarted.Should().BeFalse("断开后引擎不再持有连接");

            // 关键断言：客户端断开**不能**关掉浏览器。第二个客户端能直接连同一 endpoint 并建页，
            // 就证明 Chromium 还活着（CloseAsync 的话这里会连接被拒绝）。
            second = await Puppeteer.ConnectAsync(new ConnectOptions { BrowserWSEndpoint = endpoint! });
            var page = await second.NewPageAsync();
            page.Should().NotBeNull();
            await page.CloseAsync();
        }
        finally
        {
            // 只断开自己的连接，绝不 Dispose/Close（那会关掉共享浏览器）
            second?.Disconnect();

            TestHost.Cleanup(profile);
            await TryDeleteAsync(profile);
        }
    }

    [Fact]
    public async Task NoPage_ShouldNotCreateClientLock()
    {
        var profile = NewProfile();
        var baseDirectory = Path.Combine(profile, "discovery");
        Directory.CreateDirectory(profile);

        try
        {
            var options = Options(profile);
            var engine = new BrowserEngine(options, logger: null, baseDirectoryOverride: baseDirectory);
            await using var host = new BrowserSessionHost(
                engine, options, NullLogger<BrowserSessionHost>.Instance);

            // 只构造，不建页：presence 必须还没被创建（Ruling 6）。
            var locator = new BrowserHostLocator(profile, baseDirectory);

            Directory.Exists(locator.ClientsDirectory)
                .Should().BeFalse("未建页时不得产生 clients/<hash>/ 目录，否则宿主永远等不到空闲退出");

            BrowserClientPresence.AliveClientPids(profile, baseDirectory)
                .Should().BeEmpty("未建页时本进程不应对宿主声称「我还活着」");
        }
        finally
        {
            await TryDeleteAsync(profile);
        }
    }

    [Fact]
    public void ResolveHostCommand_ShouldCarryHostLevelOptions()
    {
        var profile = Path.Combine(Path.GetTempPath(), "seeing-host-cmd-" + Guid.NewGuid().ToString("N")[..8]);
        var options = new BrowserOptions
        {
            Headless = false,
            AntiDetection = false,
            ExecutablePath = @"C:\Edge\msedge.exe",
            UserAgent = "Mozilla/5.0 custom",
            ExtraArgs = ["--proxy-server=http://127.0.0.1:7890", "--lang=zh-CN"]
        };

        var (fileName, args) = BrowserEngine.ResolveHostCommand(profile, options);

        fileName.Should().NotBeNullOrWhiteSpace();

        // 参数名必须与 BrowserHostRunner.ParseHostStartup 一致；少传一项，
        // 共享模式下「自定义 Edge / 有头 / 追加参数」就会静默失效。
        ValueAfter(args, "--browser-host").Should().BeNull("它是模式开关，没有值");
        args.Should().Contain("--browser-host");
        ValueAfter(args, "--profile").Should().Be(profile);
        ValueAfter(args, "--headless").Should().Be("false");
        ValueAfter(args, "--anti-detection").Should().Be("false");
        ValueAfter(args, "--executable").Should().Be(@"C:\Edge\msedge.exe");
        ValueAfter(args, "--user-agent").Should().Be("Mozilla/5.0 custom");
        ValuesAfter(args, "--extra-arg").Should().Equal(
            "--proxy-server=http://127.0.0.1:7890", "--lang=zh-CN");
    }

    [Fact]
    public void ShouldRelaunch_ShouldOnlyReportHostLevelDifferences()
    {
        var options = new BrowserOptions { Headless = true, AntiDetection = true };

        var matching = Info(new BrowserHostEffective(
            Headless: true,
            ExecutablePath: @"C:\Edge\msedge.exe",   // 宿主自动探测到的绝对路径 ≠ 本地空配置
            UserAgent: null,
            AntiDetection: true,
            ExtraArgs: [],
            HostIdleSeconds: 1800));

        BrowserEngine.ShouldRelaunch(matching, options)
            .Should().BeFalse("本地没配 ExecutablePath 时，宿主自动探测的结果不算不一致");

        BrowserEngine.ShouldRelaunch(
                Info(matching.Effective! with { Headless = false }), options)
            .Should().BeTrue("Headless 只能在宿主启动时生效");

        BrowserEngine.ShouldRelaunch(
                Info(matching.Effective! with { ExtraArgs = ["--a"] }), options)
            .Should().BeTrue("追加参数只能在宿主启动时生效");

        BrowserEngine.ShouldRelaunch(
                Info(new BrowserHostEffective(true, @"C:\Edge\msedge.exe", "UA-1", true, [], 1800)),
                options)
            .Should().BeTrue("宿主 UA 与本机配置不同（本机为空、宿主有值）");

        BrowserEngine.ShouldRelaunch(new BrowserHostInfo(
                BrowserHostLocator.CurrentVersion, "gen", 1, DateTimeOffset.Now, "p",
                null, null, "ws://127.0.0.1:1/x", null, Effective: null), options)
            .Should().BeFalse("旧宿主没有 effective 时不猜，直接放行");
    }

    [Fact]
    public async Task ProbeEndpoint_ShouldAcceptHttpResponse()
    {
        var ct = TestContext.Current.CancellationToken;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            var stream = client.GetStream();
            var buffer = new byte[512];
            var read = await stream.ReadAsync(buffer, ct);
            if (read <= 0)
                return;

            var payload = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}");
            await stream.WriteAsync(payload, ct);
        }, ct);

        try
        {
            (await BrowserEngine.ProbeEndpointAsync(
                    $"ws://127.0.0.1:{port}/devtools/browser/probe",
                    TimeSpan.FromSeconds(2),
                    ct))
                .Should().BeTrue("端口上确实是 Chromium 的 HTTP 端点");
        }
        finally
        {
            listener.Stop();
            try { await server; } catch { /* 端口被提前关闭：不影响断言 */ }
        }
    }

    [Fact]
    public async Task ProbeEndpoint_ShouldRejectClosedPort()
    {
        var ct = TestContext.Current.CancellationToken;

        // 拿一个确定的空闲端口再立刻释放：之后连它必然被拒绝。
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        (await BrowserEngine.ProbeEndpointAsync(
                $"ws://127.0.0.1:{port}/devtools/browser/probe",
                TimeSpan.FromMilliseconds(300),
                ct))
            .Should().BeFalse("没有监听者的端点必须判为不可用，而不是抛异常");
    }

    private static BrowserHostInfo Info(BrowserHostEffective effective)
        => new(
            BrowserHostLocator.CurrentVersion,
            "gen",
            HostPid: 1,
            HostStartedAt: DateTimeOffset.Now,
            ProfileDirectory: Path.GetTempPath(),
            BrowserPid: null,
            BrowserStartedAt: null,
            Endpoint: "ws://127.0.0.1:1/devtools/browser/x",
            LastError: null,
            Effective: effective);

    private static string? ValueAfter(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0 || index + 1 >= args.Length)
            return null;

        return args[index + 1].StartsWith("--", StringComparison.Ordinal) ? null : args[index + 1];
    }

    private static string[] ValuesAfter(string[] args, string name)
    {
        var values = new List<string>();
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
                values.Add(args[i + 1]);
        }

        return [.. values];
    }

    private static IOptionsMonitor<BrowserOptions> Options(string profileDirectory)
        => new TestOptionsMonitor<BrowserOptions>(new BrowserOptions
        {
            Headless = true,
            IdleSeconds = 0,
            PersistLogin = true,
            ProfileDirectory = profileDirectory
        });

    private static string NewProfile()
        => Path.Combine(Path.GetTempPath(), "seeing-browser-connect-" + Guid.NewGuid().ToString("N")[..8]);

    private static async Task TryDeleteAsync(string directory)
    {
        // 宿主刚被杀掉时 Chromium 子进程可能还短暂持有 profile 内文件句柄：
        // 条件轮询重试几次再放弃（清理失败不影响断言）。
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // E5：轮询间隔（命中即返回）
                await Task.Delay(150).ConfigureAwait(false);
            }
        }
    }
}
