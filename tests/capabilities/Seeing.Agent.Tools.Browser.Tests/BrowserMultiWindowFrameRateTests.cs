using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using PuppeteerSharp;
using Seeing.Agent.Tools.Browser;
using Xunit;

namespace Seeing.Agent.Tools.Browser.Tests;

/// <summary>
/// 多路画面流验收：同一个浏览器里两个<b>窗口</b>页必须同时出帧。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么必须是真实浏览器用例</b>：这个能力的事实完全在 Chromium 内部 ——
/// screencast 只为「可见」的页面出帧。默认标签页模式下，第二个页建出来就把第一个挤到后台，
/// 后台那一路实测掉到 <b>0 帧</b>（切回前台又恢复）。假引擎只能测到「我们调用了建页方法」，
/// 测不到「谁在出帧」。
/// </para>
/// <para>
/// <b>默认跳过</b>：需要真实浏览器。本机跑：<c>$env:SEEING_BROWSER_TESTS=1</c>。
/// </para>
/// </remarks>
[Trait("Category", "RequiresBrowser")]
public class BrowserMultiWindowFrameRateTests
{
    /// <summary>验收下限：每路都要 ≥25fps（实测每窗约 60fps，留足余量吸收机器负载抖动）。</summary>
    private const double MinimumFps = 25;

    /// <summary>帧率统计窗口。</summary>
    private static readonly TimeSpan MeasureWindow = TimeSpan.FromSeconds(2);

    private static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable("SEEING_BROWSER_TESTS"), "1", StringComparison.Ordinal);

    [Fact]
    public async Task TwoWindowPages_ShouldBothKeepStreamingFrames()
    {
        if (!Enabled)
            return;

        var ct = TestContext.Current.CancellationToken;
        var profile = Path.Combine(Path.GetTempPath(), "multiwin-" + Guid.NewGuid().ToString("N")[..8]);
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

        try
        {
            var browser = await engine.GetBrowserAsync(ct);

            // 两页都建成窗口：第二个窗口建立后第一个就处于「非焦点」状态——
            // 正是标签页模式下掉到 0 帧的那种情形。
            var first = await BrowserPageFactory.CreateAsync(browser, context: null, ct);
            var second = await BrowserPageFactory.CreateAsync(browser, context: null, ct);

            await PrepareAsync(first, ct);
            await PrepareAsync(second, ct);

            await using var probe1 = await ScreencastProbe.StartAsync(first, ct);
            await using var probe2 = await ScreencastProbe.StartAsync(second, ct);

            // 先等两路都真的出过帧（条件轮询），再开始计时窗口，
            // 否则统计窗口里会混进启动延迟，得出的帧率偏低。
            await WaitUntilAsync(() => probe1.Frames > 0 && probe2.Frames > 0, ct);

            var baseline1 = probe1.Frames;
            var baseline2 = probe2.Frames;

            // 固定 2 秒是**测量窗口本身**（帧率 = 帧数 / 时长），属 AGENTS.md 例外表 E4：
            // 这段时长就是被测对象的定义，不是「等状态就绪」的猜测。
            var stopwatch = Stopwatch.StartNew();
            await Task.Delay(MeasureWindow, ct);
            stopwatch.Stop();

            var seconds = stopwatch.Elapsed.TotalSeconds;
            var fps1 = (probe1.Frames - baseline1) / seconds;
            var fps2 = (probe2.Frames - baseline2) / seconds;

            fps1.Should().BeGreaterThanOrEqualTo(MinimumFps,
                "第一个窗口（后建的窗口抢走焦点后处于后台）必须继续出帧；" +
                "掉到 0 说明页面被建成了标签页。实测: {0:F1}fps", fps1);
            fps2.Should().BeGreaterThanOrEqualTo(MinimumFps,
                "第二个窗口必须继续出帧。实测: {0:F1}fps", fps2);

            await first.CloseAsync();
            await second.CloseAsync();
        }
        finally
        {
            await engine.ShutdownAsync(CancellationToken.None);
            await TryDeleteAsync(profile);
        }
    }

    /// <summary>
    /// 让页面持续产生画面变化（screencast 只在有 damage 时出帧）。
    /// </summary>
    private static async Task PrepareAsync(IPage page, CancellationToken ct)
    {
        await page.SetViewportAsync(new ViewPortOptions { Width = 800, Height = 600 });
        await page.SetContentAsync("""
            <html><body style="margin:0;background:#fff">
            <canvas id="c" width="800" height="600"></canvas>
            <script>
              const ctx = document.getElementById('c').getContext('2d');
              let t = 0;
              function frame() {
                t = (t + 4) % 360;
                ctx.fillStyle = 'hsl(' + t + ',80%,50%)';
                ctx.fillRect(0, 0, 800, 600);
                ctx.fillStyle = '#fff';
                ctx.font = '40px sans-serif';
                ctx.fillText(String(t), 40, 80);
                requestAnimationFrame(frame);
              }
              requestAnimationFrame(frame);
            </script>
            </body></html>
            """);
    }

    /// <summary>条件轮询等某状态成立（不猜固定时长）。</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(10))
                throw new TimeoutException("两路画面流在 10 秒内没有同时出帧");

            // 轮询间隔（AGENTS.md 例外表 E5）：命中即返回，不是固定等待。
            await Task.Delay(50, ct);
        }
    }

    private static async Task TryDeleteAsync(string dir)
    {
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
                // 浏览器进程可能仍在释放句柄：清理失败不影响断言
                await Task.Delay(150);
            }
        }
    }

    /// <summary>
    /// 页级 screencast 探针：数帧并逐帧 ack（不 ack Chromium 就不再推下一帧）。
    /// </summary>
    /// <remarks>
    /// 刻意不复用 <c>BrowserScreencastHub</c>：那是给宿主用的多观看者广播，
    /// 这里要的是「这一页到底有没有在出帧」的最小观测，不引入广播与背压变量。
    /// </remarks>
    private sealed class ScreencastProbe : IAsyncDisposable
    {
        private readonly ICDPSession _cdp;
        private int _frames;

        private ScreencastProbe(ICDPSession cdp) => _cdp = cdp;

        /// <summary>已收到的帧数。</summary>
        public int Frames => Volatile.Read(ref _frames);

        public static async Task<ScreencastProbe> StartAsync(IPage page, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var cdp = await page.CreateCDPSessionAsync();
            var probe = new ScreencastProbe(cdp);

            cdp.MessageReceived += probe.OnMessageReceived;
            await cdp.SendAsync("Page.startScreencast", new Dictionary<string, object>
            {
                ["format"] = "jpeg",
                ["quality"] = 40,
                ["maxWidth"] = 800,
                ["maxHeight"] = 600,
                ["everyNthFrame"] = 1
            });

            return probe;
        }

        private void OnMessageReceived(object? sender, MessageEventArgs e)
        {
            if (!string.Equals(e.MessageID, "Page.screencastFrame", StringComparison.Ordinal))
                return;

            Interlocked.Increment(ref _frames);

            try
            {
                using var document = JsonDocument.Parse(e.MessageData.ToString() ?? "{}");
                if (!document.RootElement.TryGetProperty("sessionId", out var session)
                    || session.ValueKind != JsonValueKind.Number)
                {
                    return;
                }

                _ = _cdp.SendAsync("Page.screencastFrameAck", new Dictionary<string, object>
                {
                    ["sessionId"] = session.GetInt32()
                }).ContinueWith(
                    static t => _ = t.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
            }
            catch (JsonException)
            {
                // 帧数据解析失败不该中断统计：少一次 ack，后续帧仍会继续
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cdp.MessageReceived -= OnMessageReceived;

            try
            {
                await _cdp.SendAsync("Page.stopScreencast", new Dictionary<string, object>());
            }
            catch
            {
                // 页面可能已关闭
            }

            try
            {
                await _cdp.DetachAsync();
            }
            catch
            {
                // 同上
            }
        }
    }
}
