using System.Diagnostics;
using FluentAssertions;
using Seeing.Agent.Tools.Browser;
using Xunit;

namespace Seeing.Agent.Tools.Browser.Tests;

/// <summary>
/// networkidle 的行为守门（真实浏览器）：空闲页面必须「真的判定到空闲」，
/// 而不是像切片版那样必然睡满整个预算。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么这条必须在真浏览器上跑</b>：问题恰恰出在「等待窗口与强制超时同时到点」，
/// 用 fake 页面只会复刻我们的错误假设。空闲页面上原生
/// <c>Timeout=3000/IdleTime=500</c> 约 500ms 返回，而切片版跑满 3013ms —— 只有真实网络栈能暴露。
/// </para>
/// <para>
/// <b>默认跳过</b>：需要真实浏览器。本机跑：<c>set SEEING_BROWSER_TESTS=1</c>。
/// </para>
/// </remarks>
[Collection(BrowserHostCollection.Name)]
[Trait("Category", "RequiresBrowser")]
public class BrowserWaitNetworkIdleTests
{
    private static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable("SEEING_BROWSER_TESTS"), "1", StringComparison.Ordinal);

    [Fact]
    public async Task NetworkIdle_OnIdlePage_ShouldReturnAfterIdleWindowNotFullBudget()
    {
        if (!Enabled)
            return;   // 未开开关：视为通过（见类型注释）

        await using var scope = await PageScope.CreateAsync("s_idle");

        var stopwatch = Stopwatch.StartNew();
        var result = await scope.Page.WaitForAsync(
            new BrowserWaitCondition(BrowserWaitKind.NetworkIdle, null),
            TimeSpan.FromMilliseconds(10_000),
            scope.Token);
        stopwatch.Stop();

        result.Satisfied.Should().BeTrue();
        result.BudgetExhausted.Should().BeFalse(
            "空闲页面不存在未完成请求，必须命中空闲判定，而不是耗尽预算后「按就绪处理」");
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(
            5_000,
            "空闲判定应在 IdleTime（500ms）量级完成；跑到预算说明空闲窗口观察不到");
    }

    [Fact]
    public async Task NetworkIdle_OnAlwaysBusyPage_ShouldExhaustBudget()
    {
        if (!Enabled)
            return;

        await using var scope = await PageScope.CreateAsync("s_busy");

        // 每 100ms 一次请求：任何 500ms 窗口里都有请求，网络永远不会「连续空闲」
        await scope.Page.EvaluateAsync(
            "(() => { window.__probe = setInterval(() => fetch('/?x=' + Date.now()), 100); return true; })()",
            awaitPromise: true,
            scope.Token);

        var result = await scope.Page.WaitForAsync(
            new BrowserWaitCondition(BrowserWaitKind.NetworkIdle, null),
            TimeSpan.FromMilliseconds(1_500),
            scope.Token);

        result.Satisfied.Should().BeTrue("网络不空闲不算页面失败");
        result.BudgetExhausted.Should().BeTrue("持续有请求时应当如实报告预算用尽");
    }

    /// <summary>真实浏览器页面 + 本地站点，作用域结束时一并收掉。</summary>
    private sealed class PageScope : IAsyncDisposable
    {
        private readonly string _profile;
        private readonly BrowserEngine _engine;
        private readonly BrowserSessionHost _host;
        private readonly TestSite _site;

        private PageScope(
            string profile,
            BrowserEngine engine,
            BrowserSessionHost host,
            TestSite site,
            BrowserPageHandle page,
            CancellationToken token)
        {
            _profile = profile;
            _engine = engine;
            _host = host;
            _site = site;
            Page = page;
            Token = token;
        }

        public BrowserPageHandle Page { get; }

        public CancellationToken Token { get; }

        public static async Task<PageScope> CreateAsync(string sessionId)
        {
            var site = await TestSite.StartAsync().ConfigureAwait(false);
            var profile = Path.Combine(Path.GetTempPath(), "wait-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(profile);

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

            TestHost.Start(profile, options.CurrentValue);

            var engine = new BrowserEngine(options, logger: null, baseDirectoryOverride: profile);
            var host = new BrowserSessionHost(engine, options);
            var page = await host.GetPageAsync(sessionId, site.BaseUrl, CancellationToken.None)
                .ConfigureAwait(false);

            return new PageScope(profile, engine, host, site, page, CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            await _host.ShutdownAsync().ConfigureAwait(false);
            await _engine.ShutdownAsync().ConfigureAwait(false);
            await _site.DisposeAsync().ConfigureAwait(false);
            TestHost.Cleanup(_profile);

            for (var i = 0; i < 40; i++)
            {
                try
                {
                    if (Directory.Exists(_profile))
                        Directory.Delete(_profile, recursive: true);
                    return;
                }
                catch
                {
                    // 宿主进程释放 profile 需要一点时间：短暂重试后放弃（随系统回收）
                    await Task.Delay(100).ConfigureAwait(false);
                }
            }
        }
    }
}
