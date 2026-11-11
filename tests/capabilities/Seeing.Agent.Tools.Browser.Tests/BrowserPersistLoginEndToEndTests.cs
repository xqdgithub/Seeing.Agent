using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Seeing.Agent.Tools.Browser;
using Xunit;

namespace Seeing.Agent.Tools.Browser.Tests;

/// <summary>
/// 持久登录端到端守门：cookie 必须真的跨浏览器重启存活，且同一 profile 的第二个会话能看到它。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么必须是真实浏览器用例</b>：这个能力的核心事实是「Chromium 只有默认上下文会写盘」。
/// 用假引擎测只能测到「我们调用了 NewPageAsync」，测不到 cookie 到底有没有落地 ——
/// 而错用隔离上下文时的症状正是「代码看起来完全正确，但重启后登录态为 0」。
/// </para>
/// <para>
/// <b>默认跳过</b>：需要真实浏览器。本机跑：<c>set SEEING_BROWSER_TESTS=1</c>。
/// </para>
/// </remarks>
[Trait("Category", "RequiresBrowser")]
public class BrowserPersistLoginEndToEndTests
{
    private static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable("SEEING_BROWSER_TESTS"), "1", StringComparison.Ordinal);

    [Fact]
    public async Task Cookie_ShouldSurviveBrowserRestart()
    {
        if (!Enabled)
            return;

        var ct = TestContext.Current.CancellationToken;
        var profileDir = Path.Combine(Path.GetTempPath(), "seeing-persist-" + Guid.NewGuid().ToString("N")[..8]);

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
            var options = new TestOptionsMonitor<BrowserOptions>(new BrowserOptions
            {
                Headless = true,
                IdleSeconds = 0,
                PersistLogin = true,
                ProfileDirectory = profileDir
            });

            // ── 第一轮：登录，然后彻底关停（模拟进程重启）──
            {
                await using var provider = BuildProvider(options);
                var host = provider.GetRequiredService<IBrowserSessionHost>();

                var page = await host.GetPageAsync("s_login", $"http://127.0.0.1:{port}/login", ct);
                var cookies = await page.Page.GetCookiesAsync();
                cookies.Should().Contain(c => c.Name == "SEEING_AUTH",
                    "登录页必须真的把 cookie 种进浏览器");

                await provider.GetRequiredService<BrowserSessionHost>().ShutdownAsync(ct);
                await provider.GetRequiredService<BrowserEngine>().ShutdownAsync(ct);
            }

            // 给 Chromium 一点时间把内存中的 cookie 刷到磁盘（关停返回时写盘可能仍在进行）。
            await WaitForFileAsync(Path.Combine(profileDir, "Default", "Network", "Cookies"), ct);

            // ── 第二轮：同一 profile 重新启动，登录态必须还在 ──
            {
                await using var provider = BuildProvider(options);
                var host = provider.GetRequiredService<IBrowserSessionHost>();

                var page = await host.GetPageAsync("s_verify", $"http://127.0.0.1:{port}/", ct);
                var received = await page.EvaluateAsync(
                    "document.getElementById('r').innerText", true, ct);

                received.Should().NotBeNull();
                received!.ToString().Should().Contain("SEEING_AUTH=persisted-abc",
                    "持久 profile 必须让 cookie 跨重启存活；" +
                    "若这里为空，说明用了隔离上下文（cookie 不落盘）");
            }
        }
        finally
        {
            stop.Cancel();
            try { listener.Stop(); } catch { }
            try { await server.ConfigureAwait(false); } catch { }

            await TryDeleteAsync(profileDir);
        }
    }

    [Fact]
    public async Task PersistLoginDisabled_ShouldNotShareCookieAcrossSessions()
    {
        if (!Enabled)
            return;

        var ct = TestContext.Current.CancellationToken;
        var profileDir = Path.Combine(Path.GetTempPath(), "seeing-isolated-" + Guid.NewGuid().ToString("N")[..8]);

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
            var options = new TestOptionsMonitor<BrowserOptions>(new BrowserOptions
            {
                Headless = true,
                IdleSeconds = 0,
                PersistLogin = false,          // 隔离上下文
                ProfileDirectory = profileDir
            });

            await using var provider = BuildProvider(options);
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
            try { await server.ConfigureAwait(false); } catch { }

            await TryDeleteAsync(profileDir);
        }
    }

    private static ServiceProvider BuildProvider(IOptionsMonitor<BrowserOptions> options)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(options);
        services.AddSingleton<BrowserEngine>();
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

    /// <summary>等文件出现（条件轮询，不猜固定时长）。</summary>
    private static async Task WaitForFileAsync(string path, CancellationToken ct)
    {
        for (var i = 0; i < 100 && !File.Exists(path); i++)
            await Task.Delay(50, ct).ConfigureAwait(false);
    }

    private static async Task TryDeleteAsync(string dir)
    {
        // 浏览器进程可能仍在释放句柄：重试几次再放弃（清理失败不影响断言）
        for (var i = 0; i < 20; i++)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
                return;
            }
            catch
            {
                await Task.Delay(150).ConfigureAwait(false);
            }
        }
    }
}
