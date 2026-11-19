using System.Diagnostics;
using FluentAssertions;
using Seeing.Agent.Tools.Browser;
using Xunit;

namespace Seeing.Agent.Tools.Browser.Tests;

/// <summary>
/// 浏览器宿主进程：loopback endpoint 解析 + Windows Job（assign self）约束。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不给测试进程 <c>AssignCurrentProcess</c></b>：Job 带
/// <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>，句柄一关，job 内所有进程被杀。
/// 若把测试进程自己 assign 进去，<c>Dispose</c> 就会把测试进程杀掉 ——
/// 表现为「测试跑着跑着整个 runner 消失」，且没有任何失败信息。
/// 这里只用子进程验证约束。
/// </para>
/// </remarks>
public class BrowserProcessHostTests
{
    [Fact]
    public void ParseDevToolsEndpoint_ShouldBuildLoopbackUrl()
        => BrowserProcessHost.ParseDevToolsEndpoint("52341\n/devtools/browser/abc")
            .Should().Be("ws://127.0.0.1:52341/devtools/browser/abc");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("52341")]                                   // 只有端口，没有 browser 路径
    [InlineData("abc\n/devtools/browser/abc")]              // 端口不是数字
    [InlineData("0\n/devtools/browser/abc")]                // 端口必须为正
    [InlineData("70000\n/devtools/browser/abc")]            // 越界端口
    [InlineData("52341\n")]                                 // 路径为空
    [InlineData("52341\ndevtools/browser/abc")]             // 路径不以 / 开头
    [InlineData("52341\n/foo")]                             // 路径不是 browser 级 CDP 路径
    [InlineData("52341\n/devtools/page/abc")]               // page 级路径不能作宿主 endpoint
    public void ParseDevToolsEndpoint_ShouldReturnNullForMalformedContent(string content)
        => BrowserProcessHost.ParseDevToolsEndpoint(content).Should().BeNull();

    [Fact]
    public void ParseDevToolsEndpoint_ShouldTolerateWindowsNewlinesAndTrailingWhitespace()
        => BrowserProcessHost.ParseDevToolsEndpoint("52341\r\n/devtools/browser/abc\r\n\r\n")
            .Should().Be("ws://127.0.0.1:52341/devtools/browser/abc");

    [Fact]
    public async Task JobObject_WhenDisposed_ShouldKillAssignedChild()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var job = BrowserJobObject.Create();
        job.Should().NotBeNull();

        // 用 ping 而不是 timeout /t：`timeout` 在 stdin 被重定向时直接报
        // "Input redirection is not supported" 并立刻退出，测试进程的 stdin 由 runner 决定，
        // 那种情况下「子进程先自己退了」会让本用例假失败。ping 不读 stdin，稳定跑满 60 秒。
        using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 60 127.0.0.1 > nul")
        { CreateNoWindow = true, UseShellExecute = false })!;

        try
        {
            job!.TryAssign(child.Id).Should().BeTrue();

            // Dispose 前必须真正存活，否则「被杀」这个结论无从谈起。
            child.HasExited.Should().BeFalse();

            job.Dispose();

            (await TestWait.UntilAsync(() => child.HasExited, TimeSpan.FromSeconds(5))).Should().BeTrue();
        }
        finally
        {
            try
            {
                if (!child.HasExited)
                    child.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // 进程在检查与 Kill 之间自行退出：清理意图已达成
            }
        }
    }

    [Fact]
    public void ShouldSkipNestedJob_ShouldRequireKernelProof()
    {
        // 「宿主声称已在 Job 内」只是意图；self-assign 失败时（受限容器 / 外层 job 禁止
        // breakaway）不能把浏览器也变成完全没有兜底。
        BrowserProcessHost.ShouldSkipNestedJob(hostAlreadyInJob: true, hostProvenInJob: true)
            .Should().BeTrue("内核证明宿主确实在 Job 内时，浏览器由祖先 Job 天然覆盖");

        BrowserProcessHost.ShouldSkipNestedJob(hostAlreadyInJob: true, hostProvenInJob: false)
            .Should().BeFalse("证明不了就必须回落到为浏览器进程单独建 Job");

        BrowserProcessHost.ShouldSkipNestedJob(hostAlreadyInJob: false, hostProvenInJob: true)
            .Should().BeFalse("调用方没声称在 Job 内时按常规建 Job");

        BrowserProcessHost.ShouldSkipNestedJob(hostAlreadyInJob: false, hostProvenInJob: false)
            .Should().BeFalse();
    }

    [Fact]
    public void IsCurrentProcessInJob_ShouldNotThrow()
    {
        // 这条判定跑在宿主启动路径上：P/Invoke 写错会让整个宿主起不来。
        var act = () => BrowserJobObject.IsCurrentProcessInJob();
        act.Should().NotThrow();

        if (!OperatingSystem.IsWindows())
            BrowserJobObject.IsCurrentProcessInJob().Should().BeFalse("Job 是 Windows 内核对象");
    }

    [Fact]
    [Trait("Category", "RequiresBrowser")]
    public async Task StartAsync_ShouldExposeLoopbackEndpointAndReapBrowserOnDispose()
    {
        // 双重门禁：trait 让 CI 能整类过滤掉；环境变量让默认 `dotnet vstest` 不白起一个浏览器。
        if (Environment.GetEnvironmentVariable("SEEING_BROWSER_TESTS") != "1")
            Assert.Skip("需要真实浏览器：设置 SEEING_BROWSER_TESTS=1 后运行");

        if (!BrowserEngine.ResolveExecutable(new BrowserOptions()).Found)
            Assert.Skip("本机没有可用 Chromium / Edge，跳过真实启动用例");

        var profile = Path.Combine(
            Path.GetTempPath(), "seeing-browser-host-test-" + Guid.NewGuid().ToString("N"));

        var options = new BrowserOptions
        {
            Headless = true,
            PersistLogin = true,
            ProfileDirectory = profile,
            IdleSeconds = 0,
        };

        int pid;
        try
        {
            await using (var host = await BrowserProcessHost.StartAsync(options, TestContext.Current.CancellationToken))
            {
                host.Endpoint.Should().StartWith("ws://127.0.0.1:");
                host.BrowserPid.Should().BeGreaterThan(0);
                host.BrowserStartedAt.Should().BeCloseTo(DateTimeOffset.Now, TimeSpan.FromMinutes(1));
                host.Browser.IsConnected.Should().BeTrue();
                pid = host.BrowserPid;
            }

            (await TestWait.UntilAsync(() => !IsProcessAlive(pid), TimeSpan.FromSeconds(10)))
                .Should().BeTrue("DisposeAsync 必须关掉浏览器进程，否则宿主退出后会留下孤儿进程");
        }
        finally
        {
            TryDeleteDirectory(profile);
        }
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false; // 进程已不存在
        }
        catch (InvalidOperationException)
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
            // 临时 profile 残留不影响断言结果
        }
    }
}
