using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Seeing.Agent.Tools.Browser.Tests;

/// <summary>
/// 死客户端页面回收：只关「锁已释放」的客户端登记的 target，活客户端的 target 绝不能动。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么两条都要测</b>：这条路径上两个方向的错误代价完全不同 ——
/// 漏关会让崩溃客户端的页面永久占着内存（不报错、不可见）；
/// <b>误关</b>会把另一个正在用的客户端的页面直接关掉（用户看到「我的标签页自己没了」）。
/// 只测「死客户端被回收」发现不了误杀。
/// </para>
/// <para>
/// 用真实宿主（<see cref="BrowserProcessHost.StartAsync"/>，真实 Chromium）+ 手工造的
/// <c>&lt;pid&gt;.lock</c> / <c>&lt;pid&gt;.pages.json</c>：存活判定依赖文件锁的内核语义，
/// mock 不出结论。
/// </para>
/// </remarks>
[Collection(BrowserHostCollection.Name)]
[Trait("Category", "RequiresBrowser")]
public class BrowserClientReclaimerTests
{
    [Fact]
    public async Task DeadClientTargets_ShouldBeClosed_WhileLiveClientTargetsSurvive()
    {
        if (!BrowserTestGate.Enabled)
            Assert.Skip("需要真实浏览器：设置 SEEING_BROWSER_TESTS=1 后运行");

        if (!BrowserEngine.ResolveExecutable(new BrowserOptions()).Found)
            Assert.Skip("本机没有可用 Chromium / Edge，跳过真实启动用例");

        var ct = TestContext.Current.CancellationToken;
        var profile = Path.Combine(
            Path.GetTempPath(), "seeing-browser-reclaim-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(profile);

        var options = new BrowserOptions
        {
            Headless = true,
            PersistLogin = true,
            ProfileDirectory = profile,
            IdleSeconds = 0,
        };

        var locator = new BrowserHostLocator(profile, profile);
        Directory.CreateDirectory(locator.ClientsDirectory);

        // 文件名里的 PID 只是标识：存活判定看的是「谁持着文件锁」，不是进程是否存在。
        const int DeadPid = 999_901;
        const int LivePid = 999_902;

        var host = await BrowserProcessHost.StartAsync(options, ct);

        PuppeteerSharp.IPage? deadPage = null;
        PuppeteerSharp.IPage? livePage = null;
        FileStream? liveLock = null;
        try
        {
            deadPage = await host.Browser.NewPageAsync();
            livePage = await host.Browser.NewPageAsync();

            var deadTarget = await ResolveTargetIdAsync(deadPage);
            var liveTarget = await ResolveTargetIdAsync(livePage);

            // 死客户端：留下了锁文件，但没有任何进程持着它。
            File.WriteAllText(
                Path.Combine(locator.ClientsDirectory, DeadPid + BrowserClientPresence.LockFileExtension),
                string.Empty);
            WritePages(locator, DeadPid, deadTarget);

            // 活客户端：本测试进程持着该 pid 的锁文件（FileShare.None）。
            liveLock = new FileStream(
                Path.Combine(locator.ClientsDirectory, LivePid + BrowserClientPresence.LockFileExtension),
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.None);
            WritePages(locator, LivePid, liveTarget);

            var closed = await BrowserClientReclaimer.ReclaimAsync(
                host.Browser, locator, logger: null, ct);

            closed.Should().BeGreaterThanOrEqualTo(1, "死客户端登记的 target 必须被关闭");

            (await TestWait.UntilAsync(() => deadPage.IsClosed, TimeSpan.FromSeconds(10)))
                .Should().BeTrue("死客户端的遗留页面必须被关掉，否则会永久占着渲染进程");

            livePage.IsClosed.Should().BeFalse("活客户端的页面被误关是用户可见的数据丢失");

            File.Exists(Path.Combine(
                    locator.ClientsDirectory, DeadPid + BrowserClientPresence.LockFileExtension))
                .Should().BeFalse("死客户端的锁文件必须被清掉，否则每轮扫描都会重试");

            File.Exists(Path.Combine(
                    locator.ClientsDirectory, LivePid + BrowserClientPresence.LockFileExtension))
                .Should().BeTrue("活客户端的锁文件绝不能删 —— 删了下一轮它就会被判成死客户端");
        }
        finally
        {
            liveLock?.Dispose();

            if (livePage is not null)
            {
                try { await livePage.CloseAsync(); } catch { /* 页面可能已被回收 */ }
            }

            if (deadPage is not null)
            {
                try { await deadPage.CloseAsync(); } catch { /* 已关闭 */ }
            }

            // 必须先释放宿主（关浏览器）再删 profile：否则 Chromium 仍持着文件句柄，
            // 删除会白等 10 秒并留下临时目录。
            await host.DisposeAsync();
            await TryDeleteAsync(profile);
        }
    }

    /// <summary>读页面的 CDP targetId（页面归属清单的键）。</summary>
    private static async Task<string> ResolveTargetIdAsync(PuppeteerSharp.IPage page)
    {
        var response = await page.Client.SendAsync("Target.getTargetInfo");
        response.Should().NotBeNull();

        using var document = JsonDocument.Parse(response!.Value.GetRawText());
        var targetId = document.RootElement.GetProperty("targetInfo").GetProperty("targetId").GetString();

        targetId.Should().NotBeNullOrWhiteSpace("没有 targetId 就无法构造归属清单，用例前提不成立");
        return targetId!;
    }

    private static void WritePages(BrowserHostLocator locator, int clientPid, string targetId)
        => File.WriteAllText(
            Path.Combine(locator.ClientsDirectory, clientPid + BrowserClientPresence.PagesFileExtension),
            JsonSerializer.Serialize(new { targetIds = new[] { targetId } }));

    private static async Task TryDeleteAsync(string directory)
    {
        // Chromium 可能仍短暂持有 profile 内文件句柄：条件轮询重试几次再放弃。
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
