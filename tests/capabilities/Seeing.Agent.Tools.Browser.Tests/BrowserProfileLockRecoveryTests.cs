using FluentAssertions;
using Xunit;

namespace Seeing.Agent.Tools.Browser.Tests;

/// <summary>
/// 持久 profile 撞锁后的恢复路径守门。
/// </summary>
/// <remarks>
/// <para>
/// <b>范围</b>：只硬化「断连/关停后的二次 Launch 撞锁」路径。
/// 健康的切会话应复用同一进程（<c>BrowserEvidenceProbes.SecondSession_ShouldReuseSameBrowserProcess_NotRelaunch</c>），
/// 不应走到 Launch；若切会话仍报占用，先查为什么会二次 Launch，而不是只靠本文件的恢复逻辑。
/// </para>
/// <para>
/// 旧缺陷：KillLeftover 返回 0 就 throw、profile 锁进 10 秒冷却、Close 后立刻删 PID——
/// 会把「锁马上会放」做成假性永久失败。
/// </para>
/// </remarks>
public class BrowserProfileLockRecoveryTests
{
    [Fact]
    public void LaunchAsync_ShouldRetryAfterProfileLockEvenWhenKillFindsNothing()
    {
        // 锚定到 profile 锁分支，避免 LaunchAsync 前半段截断漏掉重试行
        var lockBranch = ReadEngineRegion("profile 被残留浏览器进程占用");
        lockBranch.Should().NotBeNull();

        // 旧缺陷：if (killed == 0) throw; —— PID 刚被干净路径删掉、子进程还握锁时永久失败
        lockBranch.Should().NotContain("if (killed == 0)",
            "杀不到残留进程时仍应进入 RetryLaunchAfterCleanupAsync 等锁释放，" +
            "而不是立刻放弃（切会话再开时最常见）");
        lockBranch.Should().Contain("RetryLaunchAfterCleanupAsync",
            "profile 锁必须走条件轮询重试");
    }

    [Fact]
    public void GetBrowserAsync_ShouldSkipCooldownForProfileLock()
    {
        var get = ReadEngineRegion("public async Task<IBrowser> GetBrowserAsync(");
        get.Should().NotBeNull();
        get.Should().Contain("IsProfileLockFailure",
            "profile 锁与「找不到可执行文件」一样是可恢复失败，不得进 10 秒冷却；" +
            "否则用户切会话连点会被缓存错误卡住");
    }

    [Fact]
    public void DisposeBrowserQuietly_ShouldKeepPidWhileProcessStillAlive()
    {
        var dispose = ReadEngineRegion("private async Task DisposeBrowserQuietlyAsync(");
        dispose.Should().NotBeNull();
        dispose.Should().Contain("HasExited",
            "CloseAsync 返回时子进程可能还握着 Singleton 锁；进程未退出就删 PID，" +
            "下次撞锁时 KillLeftover 找不到目标，只能干瞪眼");
        dispose.Should().Contain("WaitForExitAsync",
            "应尽量等主进程退出再删 PID");
    }

    private static string? ReadEngineRegion(string marker)
    {
        var path = Path.Combine(
            FindRepositoryRoot(),
            "src", "capabilities", "Seeing.Agent.Tools.Browser", "Engine", "BrowserEngine.cs");

        if (!File.Exists(path))
            return null;

        var text = File.ReadAllText(path);
        var start = text.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            return null;

        return text.Substring(start, Math.Min(3500, text.Length - start));
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(CurrentFile())!);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Seeing.Agent.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("未找到仓库根目录");
    }

    private static string CurrentFile(
        [System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
}
