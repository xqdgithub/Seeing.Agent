using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Seeing.Agent.Tools.Browser;
using Xunit;

namespace Seeing.Agent.Tools.Browser.Tests;

/// <summary>
/// 人工操作记录守门：人在画布上的操作必须被记下来，且密码字段必须脱敏。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么这两条必须钉住</b>：
/// </para>
/// <list type="bullet">
/// <item><b>记录</b>：agent 与人在同一页面上操作时，agent 看不到用户做了什么。
/// 用户自己导航/滚动后 ref 全部失效，agent 后续按旧 ref 点击会打偏——
/// 记录是「接管告知」与「事后审计」的唯一数据来源。</item>
/// <item><b>脱敏</b>：输入内容会进结构化日志（常被长期保留与转发）。
/// 明文密码进入日志是不可逆的泄露；误脱敏只是信息略少，可以接受。</item>
/// </list>
/// <para>
/// <b>默认跳过</b>：需要真实浏览器。本机跑：<c>set SEEING_BROWSER_TESTS=1</c>。
/// </para>
/// </remarks>
[Trait("Category", "RequiresBrowser")]
public class BrowserHumanActionTests
{
    private static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable("SEEING_BROWSER_TESTS"), "1", StringComparison.Ordinal);

    [Fact]
    public async Task HumanClicksAndTyping_ShouldBeRecorded()
    {
        if (!Enabled)
            return;

        var ct = TestContext.Current.CancellationToken;
        var (host, engine, profile, site) = await SetupAsync(ct);

        try
        {
            var page = await host.GetPageAsync("s_act", site.BaseUrl, ct);

            // 清空后逐个动作验证（页面创建本身可能已被记入 navigate）
            page.ClearHumanActions();
            page.RecentHumanActions.Should().BeEmpty();

            // 点击
            await page.MouseMoveAsync(100, 100, ct);
            await page.MouseDownAsync(100, 100, "left", ct);
            await page.MouseUpAsync(100, 100, "left", ct);

            page.RecentHumanActions.Should().ContainSingle(a => a.Kind == "click");

            // 滚动
            await page.ScrollByDeltaAsync(0, -300, ct);
            page.RecentHumanActions.Should().Contain(a => a.Kind == "scroll");

            // 输入（普通输入框 → 明文）
            await page.EvaluateAsync(
                "document.getElementById('name').focus()", true, ct);
            await page.TypeTextAsync("hello", ct);

            var typed = page.RecentHumanActions.Last(a => a.Kind == "type");
            typed.Value.Should().Be("hello", "普通字段不需要脱敏");
        }
        finally
        {
            await TeardownAsync(host, engine, profile, site);
        }
    }

    [Fact]
    public async Task PasswordTyping_ShouldBeMasked()
    {
        if (!Enabled)
            return;

        var ct = TestContext.Current.CancellationToken;
        var (host, engine, profile, site) = await SetupAsync(ct);

        try
        {
            var page = await host.GetPageAsync("s_pwd", site.BaseUrl, ct);
            page.ClearHumanActions();

            // 聚焦密码框后输入
            await page.EvaluateAsync(
                "document.getElementById('pwd').focus()", true, ct);
            await page.TypeTextAsync("s3cret-password", ct);

            var typed = page.RecentHumanActions.Last(a => a.Kind == "type");
            typed.Value.Should().Be("***", "密码必须脱敏：日志会长期保留，明文不可接受");
        }
        finally
        {
            await TeardownAsync(host, engine, profile, site);
        }
    }

    [Fact]
    public async Task ClearHumanActions_ShouldEmptyTheList()
    {
        if (!Enabled)
            return;

        var ct = TestContext.Current.CancellationToken;
        var (host, engine, profile, site) = await SetupAsync(ct);

        try
        {
            var page = await host.GetPageAsync("s_clear", site.BaseUrl, ct);
            await page.MouseUpAsync(50, 50, "left", ct);
            page.RecentHumanActions.Should().NotBeEmpty();

            page.ClearHumanActions();
            page.RecentHumanActions.Should().BeEmpty(
                "接管流程处理完要清空，否则下次接管会重复告知同一批操作");
        }
        finally
        {
            await TeardownAsync(host, engine, profile, site);
        }
    }

    [Fact]
    public async Task AgentToolCalls_ShouldNotBeRecordedAsHumanActions()
    {
        if (!Enabled)
            return;

        var ct = TestContext.Current.CancellationToken;
        var (host, engine, profile, site) = await SetupAsync(ct);

        try
        {
            var page = await host.GetPageAsync("s_agent", site.BaseUrl, ct);
            page.ClearHumanActions();

            // agent 侧的操作（走 RunAsync 路径）
            await page.EvaluateAsync("document.title", true, ct);
            await page.ScreenshotAsync(fullPage: false, target: null, ct);

            page.RecentHumanActions.Should().BeEmpty(
                "只有人工输入才该被记录；agent 的工具调用不能混进来（否则告知文本会把自己干的事说成用户干的）");
        }
        finally
        {
            await TeardownAsync(host, engine, profile, site);
        }
    }

    private static async Task<(IBrowserSessionHost Host, BrowserEngine Engine, string Profile, TestSite Site)>
        SetupAsync(CancellationToken ct)
    {
        var site = await TestSite.StartAsync().ConfigureAwait(false);

        var profile = Path.Combine(Path.GetTempPath(), "human-" + Guid.NewGuid().ToString("N")[..8]);
        var options = new TestOptionsMonitor<BrowserOptions>(new BrowserOptions
        {
            Headless = true,
            PersistLogin = true,
            ProfileDirectory = profile,
            IdleSeconds = 0,
            AllowPrivateHosts = true,
            ViewportWidth = 800,
            ViewportHeight = 600
        });

        var engine = new BrowserEngine(options);
        var host = new BrowserSessionHost(engine, options);
        return (host, engine, profile, site);
    }

    private static async Task TeardownAsync(
        IBrowserSessionHost host, BrowserEngine engine, string profile, TestSite site)
    {
        if (host is BrowserSessionHost concrete)
            await concrete.ShutdownAsync().ConfigureAwait(false);

        await engine.ShutdownAsync().ConfigureAwait(false);

        // 先停站点再删 profile：否则后台泵可能仍在跑，与目录删除互相干扰
        await site.DisposeAsync().ConfigureAwait(false);

        for (var i = 0; i < 20; i++)
        {
            try
            {
                if (Directory.Exists(profile))
                    Directory.Delete(profile, recursive: true);
                return;
            }
            catch
            {
                await Task.Delay(150).ConfigureAwait(false);
            }
        }
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
