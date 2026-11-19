using FluentAssertions;
using Xunit;

namespace Seeing.Agent.Tools.Browser.Tests;

/// <summary>
/// 客户端释放路径守门：与共享宿主之间只有 <c>Disconnect()</c>，**禁止**关浏览器。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么用源码断言而不是行为测试</b>：<c>CloseAsync</c> 的破坏性只在「多个客户端共享一个浏览器」时
/// 才显形（它会把别人的 Chromium 一起关掉），而单元测试里通常只有一个客户端、断掉也看不出来。
/// 设计文档明确要求「架构测试用 grep 守住」这条纪律，这里就是那道门。
/// </para>
/// <para>
/// 历史缺陷：客户端旧模型自己 <c>LaunchAsync</c> 并在关停时 <c>CloseAsync</c>。改成共享宿主后，
/// 一次客户端退出会让所有会话的页面与登录态一起消失，且没有任何报错指向这里。
/// </para>
/// </remarks>
public class BrowserProfileLockRecoveryTests
{
    [Fact]
    public void EngineReleasePath_ShouldOnlyDisconnect()
    {
        var region = ReadEngineRegion("private void DisconnectCurrent()");
        region.Should().NotBeNull("客户端的释放路径必须存在且可审计");

        region.Should().Contain("Disconnect()",
            "唯一允许的释放通道是 IBrowser.Disconnect()（PuppeteerSharp 没有 DisconnectAsync）");

        region.Should().NotContain("browser.CloseAsync",
            "客户端调用 CloseAsync 会关掉共享浏览器，杀死所有人的页面与登录态");

        region.Should().NotContain("browser.DisposeAsync",
            "客户端调用 IBrowser.DisposeAsync 同样会关掉共享浏览器");
    }

    [Fact]
    public void ShutdownAsync_ShouldNotCloseBrowser()
    {
        var region = ReadEngineRegion("public async Task ShutdownAsync(");
        region.Should().NotBeNull();

        region.Should().Contain("DisconnectCurrent",
            "模块停用只断开连接，浏览器归宿主");

        region.Should().NotContain("CloseAsync",
            "停用模块不得关闭共享浏览器");
    }

    [Fact]
    public void Engine_ShouldNotLaunchOrOwnBrowserProcess()
    {
        var source = ReadEngineSource();
        source.Should().NotBeNull();

        source.Should().NotContain("Puppeteer.LaunchAsync",
            "客户端不再拉起自己的浏览器进程 —— 那是宿主（BrowserProcessHost）的职责");

        // 只查订阅/遍历这类真正的污染调用；文档注释里提到「禁止」不算违规。
        source.Should().NotContain("TargetCreated +=",
            "禁止挂 TargetCreated 做反检测：会污染其它客户端正在使用的页面");

        source.Should().NotContain("await browser.PagesAsync(",
            "禁止遍历 PagesAsync() 做反检测：会改到别的客户端的页面");
    }

    private static string? ReadEngineSource()
    {
        var path = Path.Combine(
            FindRepositoryRoot(),
            "src", "capabilities", "Seeing.Agent.Tools.Browser", "Engine", "BrowserEngine.cs");

        return File.Exists(path) ? File.ReadAllText(path) : null;
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
