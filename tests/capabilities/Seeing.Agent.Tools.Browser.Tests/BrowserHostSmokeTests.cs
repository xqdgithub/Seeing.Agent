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
        RequireBrowser();

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

    /// <summary>
    /// 启动失败必须以**非零**退出码结束，并把原因留在发现文件里。
    /// </summary>
    /// <remarks>
    /// <b>这条用例钉的是机制性 bug</b>：宿主自持有的 Job 句柄若在方法返回时被 Dispose，
    /// <c>KILL_ON_JOB_CLOSE</c> 会在提交退出码之前把宿主自己杀掉，所有失败都伪装成 exit 0。
    /// 只看日志「失败」不够 —— 进程退出码才是客户端/运维判断的依据。
    /// </remarks>
    [Fact]
    public void Host_WithMissingExecutable_ShouldExitNonZeroAndKeepLastError()
    {
        RequireBrowser();

        var profile = NewProfile();
        var locator = new BrowserHostLocator(profile, profile);
        var missing = Path.Combine(profile, "definitely-not-a-browser.exe");

        try
        {
            var result = TestHost.RunToExit(profile, ["--executable", missing], TimeSpan.FromSeconds(90));

            result.ExitCode.Should().NotBe(0,
                "找不到浏览器是可恢复失败，但进程必须以非零退出码表达失败（Job 句柄不得提前关闭把退出码吞掉）");
            result.Discovery.Should().NotBeNull("失败原因必须留在发现文件里供设置页与人工排查");
            result.Discovery!.Endpoint.Should().BeEmpty();
            result.Discovery.LastError.Should().NotBeNullOrWhiteSpace();
            result.Discovery.BrowserPid.Should().BeNull();
        }
        finally
        {
            TestHost.Cleanup(profile);
            TryDeleteDirectory(profile);
        }
    }

    /// <summary>
    /// 无存活客户端持续 <c>HostIdleSeconds</c> → 正常退出 0，并删掉发现文件。
    /// </summary>
    /// <remarks>
    /// 断言「发现文件被删」而不是只看退出码：留着过期 endpoint 会让客户端反复重试一个不存在的端口。
    /// </remarks>
    [Fact]
    public void Host_WithShortIdle_ShouldExitZeroAndRemoveDiscovery()
    {
        RequireBrowser();

        var profile = NewProfile();
        var locator = new BrowserHostLocator(profile, profile);

        try
        {
            var result = TestHost.RunToExit(
                profile, ["--idle-seconds", "2"], TimeSpan.FromSeconds(120));

            result.ExitCode.Should().Be(0, "空闲退出是正常路径，不是错误。输出：" + result.Output);
            result.Discovery.Should().BeNull("正常退出必须删发现文件，避免客户端连到已停的 endpoint");
        }
        finally
        {
            TestHost.Cleanup(profile);
            TryDeleteDirectory(profile);
        }
    }

    /// <summary>
    /// 旧版本 <c>.seeing-engine.pid</c> 指向存活进程 → 拒绝启动并退出 1。
    /// </summary>
    /// <remarks>
    /// 用测试进程自己的 PID 充当「旧实例」：它必然存活，且不需要真的拉起一个旧版浏览器。
    /// </remarks>
    [Fact]
    public void Host_WithLiveLegacyPid_ShouldExitOne()
    {
        RequireBrowser();

        var profile = NewProfile();
        var locator = new BrowserHostLocator(profile, profile);

        try
        {
            Directory.CreateDirectory(profile);
            File.WriteAllText(
                Path.Combine(profile, ".seeing-engine.pid"),
                Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

            var result = TestHost.RunToExit(profile, [], TimeSpan.FromSeconds(90));

            result.ExitCode.Should().Be(1, "检测到旧实例必须拒绝启动。输出：" + result.Output);
            result.Discovery.Should().NotBeNull();
            result.Discovery!.LastError.Should().Contain(
                Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "lastError 必须给出占用者的 PID，用户才知道该关哪个进程");
        }
        finally
        {
            TestHost.Cleanup(profile);
            TryDeleteDirectory(profile);
        }
    }

    /// <summary>
    /// 浏览器单独崩溃 → 宿主写 <c>lastError</c> 并以非零退出码退出。
    /// </summary>
    /// <remarks>
    /// 用「直接杀浏览器进程」模拟崩溃：这正是 <c>browser.Disconnected</c> 的真实触发条件，
    /// 也顺带验证正常关停（CloseAsync）与崩溃路径不会互相误判。
    /// </remarks>
    [Fact]
    public async Task Host_WhenBrowserDies_ShouldExitNonZeroAndRecordLastError()
    {
        RequireBrowser();

        var profile = NewProfile();
        var locator = new BrowserHostLocator(profile, profile);

        try
        {
            using var host = TestHost.Start(profile);

            var info = await TestWait.UntilAsync(() => locator.Read(), TimeSpan.FromSeconds(20));
            info.Should().NotBeNull();
            var browserPid = info!.BrowserPid!.Value;

            KillProcessTree(browserPid);

            (await TestWait.UntilAsync(() => host.HasExited, TimeSpan.FromSeconds(20)))
                .Should().BeTrue("浏览器断开后宿主必须主动退出，否则会一直挂着一个死连接的 endpoint");

            // GetProcessById 得到的句柄不提供 ExitCode，读启动器（dotnet run 原样透传子进程退出码）。
            TestHost.WaitForLauncherExit(profile, TimeSpan.FromSeconds(10))
                .Should().NotBe(0, "崩溃必须以非零退出码表达失败");

            var after = locator.Read();
            after.Should().NotBeNull("崩溃原因必须保留在发现文件里");
            after!.Endpoint.Should().BeEmpty("断连后 endpoint 已不可用，不能继续发布");
            after.LastError.Should().NotBeNullOrWhiteSpace();
        }
        finally
        {
            TestHost.Cleanup(profile);
            TryDeleteDirectory(profile);
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

    private static string NewProfile()
        => Path.Combine(Path.GetTempPath(), "seeing-browser-host-smoke-" + Guid.NewGuid().ToString("N"));

    private static void KillProcessTree(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is ArgumentException
                                       or InvalidOperationException
                                       or System.ComponentModel.Win32Exception)
        {
            // 已经自行退出
        }
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
