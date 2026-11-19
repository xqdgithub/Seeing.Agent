using System.Diagnostics;
using FluentAssertions;
using Seeing.Agent.Tools.Browser;
using Xunit;

namespace Seeing.Agent.Tools.Browser.Tests;

/// <summary>
/// 宿主端到端冒烟：<c>--browser-host</c> 能发布发现文件，且宿主死则浏览器死。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么这是集成测试而不是单测</b>：要验证的正是「进程之间的生死关系」——
/// Job（Windows）/ watchdog（POSIX）是内核与操作系统语义，mock 不出结论。
/// </para>
/// <para>
/// <b>默认跳过</b>：<c>SEEING_BROWSER_TESTS=1</c> 时才跑（见 <see cref="BrowserTestGate"/>）。
/// </para>
/// </remarks>
[Trait("Category", "RequiresBrowser")]
public class BrowserHostSmokeTests
{
    [Fact]
    public async Task Host_ShouldPublishAndDieWithBrowser()
    {
        if (!BrowserTestGate.Enabled)
            Assert.Skip("需要真实浏览器：设置 SEEING_BROWSER_TESTS=1 后运行");

        if (!BrowserEngine.ResolveExecutable(new BrowserOptions()).Found)
            Assert.Skip("本机没有可用 Chromium / Edge，跳过真实启动用例");

        var profile = NewProfile();
        var locator = new BrowserHostLocator(profile, profile);

        try
        {
            using var host = TestHost.Start(profile);

            var info = await TestWait.UntilAsync(() => locator.Read(), TimeSpan.FromSeconds(20));
            info.Should().NotBeNull("宿主启动后必须写发现文件，否则客户端永远找不到它");

            info!.Version.Should().Be(BrowserHostLocator.CurrentVersion);
            info.HostPid.Should().Be(host.Id, "发现文件里的宿主 PID 必须是真实宿主进程");
            info.BrowserPid.Should().NotBeNull().And.BeGreaterThan(0);
            info.Endpoint.Should().StartWith("ws://127.0.0.1:");
            info.Effective.Should().NotBeNull("客户端要靠 effective 判断「修改需重启浏览器」");
            info.Effective!.HostIdleSeconds.Should().Be(300);

            var browserPid = info.BrowserPid!.Value;

            // 杀宿主（不进子树）：浏览器必须由 Job / watchdog 连带清掉。
            host.Kill(entireProcessTree: false);

            (await TestWait.UntilAsync(() => !IsProcessAlive(browserPid), TimeSpan.FromSeconds(10)))
                .Should().BeTrue("宿主进程死亡后浏览器必须一起退出，否则会留下占着 profile 的孤儿");
        }
        finally
        {
            TestHost.Cleanup(profile);
            TryDeleteDirectory(profile);
        }
    }

    private static string NewProfile()
        => Path.Combine(Path.GetTempPath(), "seeing-browser-host-smoke-" + Guid.NewGuid().ToString("N"));

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

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 浏览器可能还短暂持有 profile 内文件句柄：临时目录残留不影响断言。
        }
    }
}

/// <summary>
/// <see cref="BrowserProcessProbe.CommandLineUsesProfile"/> 的纯函数判定（不需要浏览器）。
/// </summary>
/// <remarks>
/// 它决定「谁占着 profile」——判错两个方向都糟：漏判会让宿主反复撞锁启动失败，
/// 误判会让宿主去杀一个只是路径前缀相同的浏览器。所以边界必须逐个钉住。
/// </remarks>
public class BrowserProcessProbeTests
{
    [Theory]
    [InlineData(@"msedge.exe --user-data-dir=C:\p\prof", @"C:\p\prof", true)]
    [InlineData(@"msedge.exe --user-data-dir=""C:\p\prof""", @"C:\p\prof", true)]
    [InlineData(@"msedge.exe --user-data-dir=""C:\p\prof\""", @"C:\p\prof", true)]
    [InlineData(@"msedge.exe --user-data-dir=C:\p\other", @"C:\p\prof", false)]
    [InlineData(@"msedge.exe --user-data-dir=C:\p\prof-backup", @"C:\p\prof", false)]
    [InlineData(@"msedge.exe --profile-directory=Default", @"C:\p\prof", false)]
    [InlineData("", @"C:\p\prof", false)]
    public void CommandLineUsesProfile_ShouldMatchOnlyExactSwitchValue(
        string commandLine, string profileDirectory, bool expected)
        => BrowserProcessProbe.CommandLineUsesProfile(commandLine, profileDirectory).Should().Be(expected);
}
