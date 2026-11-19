using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Tools.Browser;
using Xunit;

namespace Seeing.Agent.Tools.Browser.Tests;

/// <summary>
/// 宿主崩溃/被杀后，已有会话的页面句柄必须失效，并能用同一 sessionId 重建页面。
/// </summary>
/// <remarks>
/// <para>
/// <b>契约</b>：连接一断，页面句柄就不可再用。<see cref="BrowserSessionHost.TryGetPage"/> 若继续命中
/// 旧句柄，后续工具调用全部打在一个死连接上 —— 用户看到的是「重启浏览器后这个会话再也用不了」，
/// 只有重启整个进程才能恢复。
/// </para>
/// <para>
/// <b>为什么不能依赖 <c>Page.IsClosed</c></b>：PuppeteerSharp 的该属性只在收到
/// <c>Target.detachedFromTarget</c> 时变 true；宿主进程被杀时 CDP 连接直接消失，
/// 不会有任何 target 事件，句柄<b>永远</b>看起来是活的。这条用例正是钉住这个差异。
/// </para>
/// </remarks>
[Collection(BrowserHostCollection.Name)]
[Trait("Category", "RequiresBrowser")]
public class BrowserConnectionLostTests
{
    [Fact]
    public async Task HostKilled_ShouldInvalidatePageAndAllowRebuildForSameSession()
    {
        RequireBrowser();

        var ct = TestContext.Current.CancellationToken;
        var profile = Path.Combine(
            Path.GetTempPath(), "seeing-browser-connlost-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(profile);

        var optionsMonitor = new TestOptionsMonitor<BrowserOptions>(new BrowserOptions
        {
            Headless = true,
            PersistLogin = true,
            ProfileDirectory = profile,
            IdleSeconds = 0,
        });

        var engine = new BrowserEngine(optionsMonitor, logger: null, baseDirectoryOverride: profile);
        await using var host = new BrowserSessionHost(
            engine, optionsMonitor, NullLogger<BrowserSessionHost>.Instance);

        var locator = new BrowserHostLocator(profile, profile);
        var firstHost = TestHost.Start(profile, optionsMonitor.CurrentValue);
        var firstInfo = locator.Read();
        firstInfo.Should().NotBeNull("宿主必须先发布发现文件，用例才能拿到浏览器 PID");

        try
        {
            var first = await host.GetPageAsync(
                "session-rebuild", "data:text/html,<h1>before</h1>", ct);
            first.Should().NotBeNull();

            // 页面实例比对：PageClosed 必须带被作废的那个实例（hub 只清指向它的那条流）。
            var closed = new TaskCompletionSource<BrowserPageHandle>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            host.PageClosed += (_, page) =>
            {
                if (ReferenceEquals(page, first))
                    closed.TrySetResult(page);

                return Task.CompletedTask;
            };

            // 杀宿主（整棵子树）：CDP 连接随之消失，这正是 PuppeteerSharp 收不到 target 事件的路径。
            firstHost.Kill(entireProcessTree: true);

            await closed.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);

            // 旧句柄必须已经从会话表里消失：命中它后续工具调用会全部失败。
            host.TryGetPage("session-rebuild").Should().BeNull();
            host.OpenSessionCount.Should().Be(0);

            // 重拉宿主（生产里由 BrowserEngine 自动拉起；测试里显式起，避免依赖测试入口的分发能力）。
            // 先等第一条宿主的 Chromium 真正消失：profile 锁还在时新宿主会进入 15 秒重试，
            // 30 秒预算在满载机器上不够用（表现为「宿主未在超时内发布发现文件」）。
            if (firstInfo!.BrowserPid is { } firstBrowserPid)
            {
                (await TestWait.UntilAsync(() => !IsProcessAlive(firstBrowserPid), TimeSpan.FromSeconds(30)))
                    .Should().BeTrue("宿主被杀后 Chromium 必须跟着退出，否则 profile 锁会让下一次启动失败");
            }

            TestHost.Cleanup(profile);
            TestHost.Start(profile, optionsMonitor.CurrentValue, timeout: TimeSpan.FromSeconds(120));

            // 同一 sessionId 必须能重建页面，而不是继续返回死句柄。
            var rebuilt = await host.GetPageAsync(
                "session-rebuild", "data:text/html,<h1>after</h1>", ct);

            rebuilt.Should().NotBeSameAs(first, "旧页面属于已断开的连接，必须换成新页");
            rebuilt.SessionId.Should().Be("session-rebuild");
            rebuilt.Page.Url.Should().Contain("after", "新页面必须真的导航成功，而不是只返回一个空对象");
            host.TryGetPage("session-rebuild").Should().BeSameAs(rebuilt);
            host.OpenSessionCount.Should().Be(1);
        }
        finally
        {
            // 两条宿主（被杀的第一条 + 重拉的第二条）都要收掉，否则临时 profile 会残留。
            TestHost.Cleanup(profile);
            await TryDeleteAsync(profile);
        }
    }

    /// <summary>真实浏览器门禁：未开开关或本机没有 Chromium/Edge 时跳过。</summary>
    private static void RequireBrowser()
    {
        if (!BrowserTestGate.Enabled)
            Assert.Skip("需要真实浏览器：设置 SEEING_BROWSER_TESTS=1 后运行");

        if (!BrowserEngine.ResolveExecutable(new BrowserOptions()).Found)
            Assert.Skip("本机没有可用 Chromium / Edge，跳过真实启动用例");
    }

    private static bool IsProcessAlive(int processId)
    {
        if (processId <= 0)
            return false;

        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task TryDeleteAsync(string directory)
    {
        // Chromium 子进程可能在宿主被杀后短暂持有 profile 内文件句柄：
        // 条件轮询重试几次再放弃（清理失败不影响断言；E5：轮询间隔，命中即返回）。
        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(250).ConfigureAwait(false);
            }
        }
    }
}
