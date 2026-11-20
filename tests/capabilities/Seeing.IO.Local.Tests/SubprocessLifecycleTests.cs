using FluentAssertions;
using Seeing.Agent.Abstractions.Execution;
using System.Diagnostics;
using Xunit;

namespace Seeing.IO.Local.Tests;

/// <summary>
/// 子进程生命周期归属契约：默认 HostBound，显式 Detached 必须被保留。
/// </summary>
public class SubprocessLifecycleTests
{
    [Fact]
    public void Spec_DefaultLifecycle_ShouldBeHostBound()
    {
        var spec = new SubprocessSpec { FileName = "git" };

        spec.Lifecycle.Should().Be(SubprocessLifecycle.HostBound);
        SubprocessLifecycle.HostBound.Should().NotBe(SubprocessLifecycle.Detached);
    }

    [Fact]
    public void Spec_Detached_ShouldBePreserved()
    {
        var spec = new SubprocessSpec
        {
            FileName = "git",
            Lifecycle = SubprocessLifecycle.Detached,
        };

        spec.Lifecycle.Should().Be(SubprocessLifecycle.Detached);
    }

    [Fact]
    public async Task HostBound_ManyShortLivedProcesses_ShouldStillStartAndExitCleanly()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // 超过工厂内部惰性回收阈值（32），确保 Job 句柄回收路径不会打断正常启动/退出。
        var factory = new LocalSubprocessFactory();
        for (var i = 0; i < 40; i++)
        {
            using var process = factory.Start(new SubprocessSpec
            {
                FileName = "cmd",
                Arguments = "/c exit 0",
            });

            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            process.ExitCode.Should().Be(0);
        }
    }

    /// <summary>
    /// 宿主退出（含崩溃）后，HostBound 启动的后台子进程应由内核回收。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 不在 CI 主套跑的原因：
    /// </para>
    /// <list type="number">
    /// <item>仅 Windows 有 Job Object 语义；POSIX 侧本任务只留 <c>setsid</c> / 进程组 TODO。</item>
    /// <item>
    /// 观察点在被测宿主进程<b>退出之后</b>：本进程不能把自己放进带 <c>KILL_ON_JOB_CLOSE</c> 的 job
    /// （关句柄即自杀），必须另起一个宿主进程再强杀它，属于真实多进程场景，不适合进主套。
    /// </item>
    /// </list>
    /// <para>
    /// 手工验证步骤：
    /// 1. 另起一个 .NET 宿主进程，用 <see cref="LocalSubprocessFactory"/> 以 HostBound 启动
    ///    <c>cmd /c start /b ping -n 60 127.0.0.1</c>，把后台子进程 pid 写到临时文件；
    /// 2. 用 <see cref="Process.Kill()"/> 强杀宿主进程（模拟崩溃，不走任何清理代码）；
    /// 3. 断言临时文件里的 pid 已不存在 —— 说明 <c>KILL_ON_JOB_CLOSE</c> 生效。
    /// </para>
    /// </remarks>
    [Fact(Skip = "仅 Windows 且需真实子进程与独立宿主进程；不在 CI 主套跑")]
    public async Task HostBound_HostExit_ShouldKillChildren()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // 半程可执行部分：HostBound 启动器（cmd）拉起后台子进程，工厂应正常返回且不阻塞启动。
        var factory = new LocalSubprocessFactory();
        using var launcher = factory.Start(new SubprocessSpec
        {
            FileName = "cmd",
            Arguments = "/c start /b ping -n 60 127.0.0.1",
            Lifecycle = SubprocessLifecycle.HostBound,
        });

        await launcher.WaitForExitAsync(TestContext.Current.CancellationToken);
        launcher.HasExited.Should().BeTrue();

        // 「宿主退出后子进程被回收」这一步依赖独立宿主进程，见上方 remarks 的手工验证步骤。
    }
}
