using FluentAssertions;
using Seeing.Agent.Tools.Browser;
using Xunit;

namespace Seeing.Agent.Tools.Browser.Tests;

/// <summary>
/// 启动失败的解释质量：错误文案必须能指向「该做什么」，而不是只说「失败了」。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么值得单独立一套</b>：这些分支在 Windows 开发机上永远不会被真实触发
/// （缺少 <c>libnss3</c>、没有 <c>DISPLAY</c>、容器里无沙箱都是 Linux 才有的问题）。
/// 没有这层守卫，Linux 相关的文案改动可以「编译通过、测试全绿」地烂掉，
/// 而实际用户在 Ubuntu 上看到的仍然是一句无从下手的「启动浏览器失败」。
/// </para>
/// <para>
/// 测的是纯文本匹配，不需要真实浏览器进程。
/// </para>
/// </remarks>
public class BrowserLaunchFailureExplanationTests
{
    [Fact]
    public void MissingSharedLibrary_ShouldNameTheLibraryAndGiveAptCommand()
    {
        const string stderr =
            "[1108/123456.789:FATAL:zygote_host_impl_linux.cc(116)] " +
            "error while loading shared libraries: libnss3.so: cannot open shared object file: " +
            "No such file or directory";

        BrowserEngine.TryExplainLaunchFailure(stderr, out var explanation).Should().BeTrue();

        explanation.Should().Contain("libnss3.so", "要告诉用户具体缺哪个库");
        explanation.Should().Contain("apt install", "Ubuntu 上要给出可执行的命令");
        explanation.Should().Contain("libgbm1", "指引里应带上其余常见依赖，避免反复试");
    }

    [Fact]
    public void MissingSharedLibrary_WithoutLibraryName_ShouldStillGiveCommand()
    {
        // Chromium 有些版本不打印具体库名，只给一句笼统的加载失败
        const string stderr = "cannot open shared object file: No such file or directory";

        BrowserEngine.TryExplainLaunchFailure(stderr, out var explanation).Should().BeTrue();

        explanation.Should().Contain("apt install");
    }

    [Fact]
    public void MissingXServer_ShouldSuggestHeadlessOrXvfb()
    {
        const string stderr =
            "[1108/123456.789:ERROR:ozone_platform_x11.cc(238)] Missing X server or $DISPLAY";

        BrowserEngine.TryExplainLaunchFailure(stderr, out var explanation).Should().BeTrue();

        explanation.Should().Contain("无头", "首选方案是改无头模式");
        explanation.Should().Contain("xvfb", "其次才是虚拟显示");
    }

    [Fact]
    public void UnusableSandbox_ShouldSuggestNoSandboxArgs()
    {
        // Docker / 受限容器里最常见：命名空间不可用
        const string stderr = "No usable sandbox! Update your kernel or see https://...";

        BrowserEngine.TryExplainLaunchFailure(stderr, out var explanation).Should().BeTrue();

        explanation.Should().Contain("--no-sandbox");
        explanation.Should().Contain("追加启动参数", "要指明在设置页哪里改");
    }

    [Fact]
    public void UnknownFailure_ShouldNotBeExplained()
    {
        // 识别不了就返回 false：让调用方原样抛出原始异常，不能吞信息或给出错误指引
        BrowserEngine.TryExplainLaunchFailure(
            "[FATAL:something_weird.cc(1)] totally unknown problem", out var explanation)
            .Should().BeFalse();

        explanation.Should().BeNull();
    }

    [Fact]
    public void EmptyMessage_ShouldNotBeExplained()
    {
        BrowserEngine.TryExplainLaunchFailure(string.Empty, out var explanation).Should().BeFalse();
        explanation.Should().BeNull();
    }

    [Fact]
    public void ProfileLocked_ShouldNotBeMistakenForLibraryProblem()
    {
        // "The browser is already running" 有专门的处理分支（清理残留进程后重试）。
        // 如果这里误判成「缺库」，用户会去 apt install 一个根本不缺的包。
        const string message = "Failed to launch browser! The browser is already running for /home/u/.seeing/browser-profile";

        BrowserEngine.TryExplainLaunchFailure(message, out _)
            .Should().BeFalse("profile 锁有独立的重试路径，不该被当成环境缺失");
    }
}
