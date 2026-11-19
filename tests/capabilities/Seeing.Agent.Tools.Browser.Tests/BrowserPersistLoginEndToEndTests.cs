using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Seeing.Agent.Tools.Browser;
using Xunit;

namespace Seeing.Agent.Tools.Browser.Tests;

/// <summary>
/// 持久登录端到端守门：cookie 必须在客户端「断开 → 重连」后仍然在，且同一 profile 的第二个会话能看到它。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么必须是真实浏览器用例</b>：这个能力的核心事实是「Chromium 只有默认上下文会写盘」。
/// 用假引擎测只能测到「我们调用了 NewPageAsync」，测不到 cookie 到底有没有落地 ——
/// 而错用隔离上下文时的症状正是「代码看起来完全正确，但重启后登录态为 0」。
/// </para>
/// <para>
/// <b>为什么现在是「断开重连」而不是「关停重开」</b>：浏览器归宿主进程所有，客户端只连 CDP。
/// 客户端断开绝不能关掉浏览器 —— 若这里改成关停后登录态为 0，说明客户端误用了
/// <c>CloseAsync</c>/<c>DisposeAsync</c>，把共享浏览器连带杀掉了。
/// </para>
/// <para>
/// <b>默认跳过</b>：需要真实浏览器。本机跑：<c>set SEEING_BROWSER_TESTS=1</c>。
/// </para>
/// </remarks>
[Collection(BrowserHostCollection.Name)]
[Trait("Category", "RequiresBrowser")]
public class BrowserPersistLoginEndToEndTests
{
    private static bool Enabled => BrowserTestGate.Enabled;

    [Fact]
    public async Task Cookie_ShouldSurviveClientReconnect()
    {
        if (!Enabled)
            Assert.Skip("需要真实浏览器：设置 SEEING_BROWSER_TESTS=1 后运行");

        if (!BrowserEngine.ResolveExecutable(new BrowserOptions()).Found)
            Assert.Skip("本机没有可用 Chromium / Edge，跳过真实启动用例");

        var ct = TestContext.Current.CancellationToken;
        var profileDir = Path.Combine(Path.GetTempPath(), "seeing-persist-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(profileDir);

        // 本地站点：/login 种 cookie，/ 回显收到的 cookie
        using var listener = new HttpListener();
        var port = FindFreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        using var stop = new CancellationTokenSource();
        var server = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync().ConfigureAwait(false); }
                catch { return; }

                var path = ctx.Request.Url!.AbsolutePath;
                if (path == "/login")
                {
                    ctx.Response.Headers.Add(
                        "Set-Cookie",
                        "SEEING_AUTH=persisted-abc; Path=/; Max-Age=86400; HttpOnly");
                }

                var received = ctx.Request.Headers["Cookie"] ?? "";
                var body = $"<pre id=r>{WebUtility.HtmlEncode(received)}</pre>";
                var bytes = System.Text.Encoding.UTF8.GetBytes(body);
                ctx.Response.ContentType = "text/html; charset=utf-8";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
                ctx.Response.Close();
            }
        }, CancellationToken.None);

        try
        {
            // 宿主只起一次：登录态由「同一个浏览器」保留，客户端各自断开/重连。
            TestHost.Start(profileDir);

            var options = new TestOptionsMonitor<BrowserOptions>(new BrowserOptions
            {
                Headless = true,
                IdleSeconds = 0,
                PersistLogin = true,
                ProfileDirectory = profileDir
            });

            // ── 第一轮：登录，然后客户端断开（浏览器必须继续存活）──
            {
                await using var provider = BuildProvider(options, profileDir);
                var host = provider.GetRequiredService<IBrowserSessionHost>();

                var page = await host.GetPageAsync("s_login", $"http://127.0.0.1:{port}/login", ct);
                var cookies = await page.Page.GetCookiesAsync();
                cookies.Should().Contain(c => c.Name == "SEEING_AUTH",
                    "登录页必须真的把 cookie 种进浏览器");

                await provider.GetRequiredService<BrowserSessionHost>().ShutdownAsync(ct);
                await provider.GetRequiredService<BrowserEngine>().ShutdownAsync(ct);
            }

            // ── 第二轮：同一 profile 重新连接，登录态必须还在 ──
            {
                await using var provider = BuildProvider(options, profileDir);
                var host = provider.GetRequiredService<IBrowserSessionHost>();

                var page = await host.GetPageAsync("s_verify", $"http://127.0.0.1:{port}/", ct);
                var received = await page.EvaluateAsync(
                    "document.getElementById('r').innerText", true, ct);

                received.Should().NotBeNull();
                received!.ToString().Should().Contain("SEEING_AUTH=persisted-abc",
                    "断开客户端不得关掉共享浏览器；" +
                    "若这里为空，说明客户端用了 CloseAsync/DisposeAsync 把浏览器杀掉了");
            }
        }
        finally
        {
            stop.Cancel();
            try { listener.Stop(); } catch { }
            try { await server; } catch { }

            // 必须清理宿主：否则会在测试机留下一个占着 profile 的浏览器进程。
            TestHost.Cleanup(profileDir);
            await TryDeleteAsync(profileDir);
        }
    }

    [Fact]
    public async Task PersistLoginDisabled_ShouldNotShareCookieAcrossSessions()
    {
        if (!Enabled)
            Assert.Skip("需要真实浏览器：设置 SEEING_BROWSER_TESTS=1 后运行");

        if (!BrowserEngine.ResolveExecutable(new BrowserOptions()).Found)
            Assert.Skip("本机没有可用 Chromium / Edge，跳过真实启动用例");

        var ct = TestContext.Current.CancellationToken;
        var profileDir = Path.Combine(Path.GetTempPath(), "seeing-isolated-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(profileDir);

        using var listener = new HttpListener();
        var port = FindFreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        using var stop = new CancellationTokenSource();
        var server = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync().ConfigureAwait(false); }
                catch { return; }

                if (ctx.Request.Url!.AbsolutePath == "/login")
                {
                    ctx.Response.Headers.Add(
                        "Set-Cookie", "SEEING_AUTH=shared-xyz; Path=/; Max-Age=86400");
                }

                var received = ctx.Request.Headers["Cookie"] ?? "";
                var bytes = System.Text.Encoding.UTF8.GetBytes(
                    $"<pre id=r>{WebUtility.HtmlEncode(received)}</pre>");
                ctx.Response.ContentType = "text/html; charset=utf-8";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
                ctx.Response.Close();
            }
        }, CancellationToken.None);

        try
        {
            // 隔离上下文是「每个客户端各自 CreateBrowserContextAsync」，同一宿主即可验证。
            TestHost.Start(profileDir);

            var options = new TestOptionsMonitor<BrowserOptions>(new BrowserOptions
            {
                Headless = true,
                IdleSeconds = 0,
                PersistLogin = false,          // 隔离上下文
                ProfileDirectory = profileDir
            });

            await using var provider = BuildProvider(options, profileDir);
            var host = provider.GetRequiredService<IBrowserSessionHost>();

            // 会话 A 登录
            var a = await host.GetPageAsync("s_a", $"http://127.0.0.1:{port}/login", ct);
            (await a.Page.GetCookiesAsync()).Should().Contain(c => c.Name == "SEEING_AUTH");

            // 会话 B 打开同一站点：隔离上下文下不得看到 A 的 cookie
            var b = await host.GetPageAsync("s_b", $"http://127.0.0.1:{port}/", ct);
            var seenByB = await b.EvaluateAsync("document.getElementById('r').innerText", true, ct);

            seenByB.Should().NotBeNull();
            seenByB!.ToString().Should().NotContain("SEEING_AUTH",
                "PersistLogin=false 时每个会话是隔离上下文，cookie 不应串场");
        }
        finally
        {
            stop.Cancel();
            try { listener.Stop(); } catch { }
            try { await server; } catch { }

            TestHost.Cleanup(profileDir);
            await TryDeleteAsync(profileDir);
        }
    }

    /// <summary>
    /// 建一个「客户端」DI 容器：引擎只连接共享宿主，不拥有浏览器。
    /// </summary>
    /// <param name="options">配置。</param>
    /// <param name="baseDirectory">
    /// 发现文件根目录（与 <see cref="TestHost"/> 启动宿主时用的 <c>SEEING_BROWSER_BASE_DIR</c> 一致）。
    /// </param>
    private static ServiceProvider BuildProvider(
        IOptionsMonitor<BrowserOptions> options, string baseDirectory)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(options);
        services.AddSingleton(_ => new BrowserEngine(options, null, baseDirectory));
        services.AddSingleton<BrowserSessionHost>();
        services.AddSingleton<IBrowserSessionHost>(sp => sp.GetRequiredService<BrowserSessionHost>());
        return services.BuildServiceProvider();
    }

    private static int FindFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task TryDeleteAsync(string dir)
    {
        // 浏览器进程可能仍在释放句柄：重试几次再放弃（清理失败不影响断言）
        for (var i = 0; i < 40; i++)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
                return;
            }
            catch
            {
                await Task.Delay(250).ConfigureAwait(false);
            }
        }
    }
}
