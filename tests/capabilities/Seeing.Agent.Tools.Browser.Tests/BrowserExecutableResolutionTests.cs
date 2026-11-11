using FluentAssertions;
using Seeing.Agent.Tools.Browser;
using Xunit;

namespace Seeing.Agent.Tools.Browser.Tests;

/// <summary>
/// 浏览器可执行文件探测：顺序必须稳定，且「找不到」要给出可执行的补救指引。
/// </summary>
public class BrowserExecutableResolutionTests
{
    [Fact]
    public void Candidates_ShouldPreferSystemEdgeBeforeChrome()
    {
        var candidates = BrowserEngine.Candidates().Select(c => c.Path).ToArray();

        var edge = candidates.Select((path, index) => (path, index))
            .FirstOrDefault(item => item.path.EndsWith("msedge.exe", StringComparison.OrdinalIgnoreCase));
        var chrome = candidates.Select((path, index) => (path, index))
            .FirstOrDefault(item => item.path.EndsWith("chrome.exe", StringComparison.OrdinalIgnoreCase));

        edge.path.Should().NotBeNull("Windows 10/11 预装 Edge，优先复用零下载成本");
        chrome.path.Should().NotBeNull();
        edge.index.Should().BeLessThan(chrome.index, "Edge 优先于 Chrome");
    }

    [Fact]
    public void Candidates_ShouldAllBeAbsolutePaths()
    {
        foreach (var (path, _) in BrowserEngine.Candidates())
            Path.IsPathRooted(path).Should().BeTrue($"{path} 必须是绝对路径");
    }

    [Fact]
    public void ResolveExecutable_ConfiguredPathMissing_ShouldReportWhy()
    {
        var options = new BrowserOptions
        {
            ExecutablePath = Path.Combine(Path.GetTempPath(), "definitely-not-a-browser-" + Guid.NewGuid().ToString("N"))
        };

        var resolved = BrowserEngine.ResolveExecutable(options);

        resolved.Found.Should().BeFalse();
        resolved.Source.Should().Contain("配置指定的路径不存在");
    }

    [Fact]
    public void ResolveExecutable_ConfiguredPathExists_ShouldUseIt()
    {
        var fake = Path.Combine(Path.GetTempPath(), "fake-browser-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllText(fake, "not really a browser");

        try
        {
            var resolved = BrowserEngine.ResolveExecutable(new BrowserOptions { ExecutablePath = fake });

            resolved.Found.Should().BeTrue();
            resolved.Path.Should().Be(fake);
            resolved.Source.Should().Be("配置指定");
        }
        finally
        {
            File.Delete(fake);
        }
    }

    [Fact]
    public void ResolveExecutable_NoConfig_ShouldFindSomethingOrExplain()
    {
        var resolved = BrowserEngine.ResolveExecutable(new BrowserOptions());

        // 本机必然装了 Edge 或 Chrome；若开发机没有，至少来源说明必须可读
        if (resolved.Found)
        {
            File.Exists(resolved.Path).Should().BeTrue();
            resolved.Source.Should().NotBeNullOrWhiteSpace();
        }
        else
        {
            resolved.Source.Should().Contain("未找到");
        }
    }

    // ── 分平台候选清单 ────────────────────────────────────────────────
    //
    // 这些用例必须按**显式平台**跑，不能只依赖「当前运行平台」：
    // 开发机是 Windows，Linux 分支永远走不到 —— 而 Linux 恰恰是
    // 「装了 Chromium 却报未找到」的事故现场（原实现只列 Program Files 下的 .exe）。

    [Fact]
    public void LinuxCandidates_ShouldCoverDistroPackageAndSnapAndOpt()
    {
        var paths = BrowserEngine.Candidates(BrowserPlatform.Linux).Select(c => c.Path).ToArray();

        // 发行版包管理器（apt）常见落点
        paths.Should().Contain("/usr/bin/chromium");
        paths.Should().Contain("/usr/bin/chromium-browser", "Debian/Ubuntu 旧包名是 chromium-browser");
        paths.Should().Contain("/usr/bin/google-chrome-stable");
        paths.Should().Contain("/usr/bin/google-chrome");

        // 官方 deb 装到 /opt（不在 /usr/bin）
        paths.Should().Contain("/opt/google/chrome/chrome");

        // Ubuntu 上 apt 装 chromium 常常落到 snap
        paths.Should().Contain("/snap/bin/chromium");
    }

    [Fact]
    public void LinuxCandidates_ShouldAllBeUnixAbsolutePaths()
    {
        foreach (var (path, _) in BrowserEngine.Candidates(BrowserPlatform.Linux))
        {
            path.Should().StartWith("/", $"{path} 必须是 Linux 绝对路径");
            path.Should().NotContain(".exe", "Linux 可执行文件没有扩展名");
            path.Should().NotContain(":\\", "在 Windows 上生成 Linux 清单时不得混入盘符路径");
        }
    }

    [Fact]
    public void LinuxCandidates_ShouldIncludePathHitsFromProvidedEnvironment()
    {
        // 用户把浏览器解压到非标准目录、只靠 PATH 暴露时，必须能被探测到。
        // 这里注入环境而不是改进程 PATH：测试之间不产生全局副作用。
        var dir = Path.Combine(Path.GetTempPath(), "browser-path-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var chromium = Path.Combine(dir, "chromium");
        File.WriteAllText(chromium, "#!/bin/sh\n");

        try
        {
            var candidates = BrowserEngine
                .Candidates(BrowserPlatform.Linux, (dir, "/home/nobody"))
                .Select(c => c.Path)
                .ToArray();

            candidates.Should().Contain(chromium, "PATH 里的浏览器应当进入候选清单");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LinuxCandidates_ShouldIncludeDownloadedPuppeteerChrome()
    {
        // 用户按 Puppeteer 文档先下载了浏览器（不装系统包）时也应能命中，
        // 且目录按版本名降序 —— 刚下载的那版优先。
        var home = Path.Combine(Path.GetTempPath(), "browser-home-" + Guid.NewGuid().ToString("N"));
        var older = Path.Combine(home, ".cache", "puppeteer", "chrome", "linux-120", "chrome-linux64");
        var newer = Path.Combine(home, ".cache", "puppeteer", "chrome", "linux-131", "chrome-linux64");
        Directory.CreateDirectory(older);
        Directory.CreateDirectory(newer);
        File.WriteAllText(Path.Combine(older, "chrome"), string.Empty);
        File.WriteAllText(Path.Combine(newer, "chrome"), string.Empty);

        try
        {
            var paths = BrowserEngine
                .Candidates(BrowserPlatform.Linux, (null, home))
                .Select(c => c.Path)
                .ToArray();

            var olderIndex = Array.IndexOf(paths, Path.Combine(older, "chrome"));
            var newerIndex = Array.IndexOf(paths, Path.Combine(newer, "chrome"));

            olderIndex.Should().BeGreaterThanOrEqualTo(0, "旧版缓存也要被探测到");
            newerIndex.Should().BeGreaterThanOrEqualTo(0);
            newerIndex.Should().BeLessThan(olderIndex, "版本号大的应当排在前面");
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void MacCandidates_ShouldPointAtAppBundles()
    {
        var paths = BrowserEngine.Candidates(BrowserPlatform.MacOS).Select(c => c.Path).ToArray();

        paths.Should().Contain("/Applications/Google Chrome.app/Contents/MacOS/Google Chrome");
        paths.Should().Contain("/Applications/Chromium.app/Contents/MacOS/Chromium");
    }

    [Fact]
    public void Candidates_ShouldNotContainDuplicates()
    {
        // PATH 扫描与固定清单必然重叠（/usr/bin/chromium 两处都有）。
        // 不去重会让失败文案里的候选清单出现重复项，看起来像 bug。
        foreach (var platform in new[] { BrowserPlatform.Windows, BrowserPlatform.MacOS, BrowserPlatform.Linux })
        {
            var paths = BrowserEngine.Candidates(platform).Select(c => c.Path).ToArray();
            paths.Should().OnlyHaveUniqueItems($"{platform} 的候选清单不应有重复路径");
        }
    }

    [Fact]
    public void WindowsCandidates_ShouldIncludeUserLevelEdge()
    {
        // 用户级 Edge（非管理员安装）不在 Program Files 下。
        var paths = BrowserEngine.Candidates(BrowserPlatform.Windows).Select(c => c.Path).ToArray();

        paths.Should().Contain(
            p => p.Contains("msedge.exe", StringComparison.OrdinalIgnoreCase)
                 && p.Contains("Local", StringComparison.OrdinalIgnoreCase),
            "用户级 Edge 安装（%LOCALAPPDATA%）也要能被探测到");
    }

    [Fact]
    public void MustDisableSandbox_ShouldBeFalseOnWindows()
    {
        // Windows 上 Chromium 没有 root 限制；多传 --no-sandbox 只会削弱防护
        if (OperatingSystem.IsWindows())
            BrowserEngine.MustDisableSandbox().Should().BeFalse();
    }

    [Fact]
    public void BuildLaunchArgs_AntiDetectionOnRoot_ShouldAddNoSandbox()
    {
        // ── 这是 Ubuntu 服务器上的头号阻塞点 ──
        // Chromium 以 root 运行且未加 --no-sandbox 时**硬拒绝启动**
        // （"Running as root without --no-sandbox is not supported"）。
        // 反检测模式原本刻意不加该参数，于是在 root 环境下
        // 「反检测开着 = 浏览器完全起不来」，且错误只显示「启动失败」。
        var args = BrowserEngine.BuildLaunchArgs(new BrowserOptions { AntiDetection = true });

        if (BrowserEngine.MustDisableSandbox())
            args.Should().Contain("--no-sandbox", "root 下不加就完全起不来");
        else
            args.Should().NotContain("--no-sandbox", "非 root 时应保持最干净的指纹");
    }

    [Fact]
    public void BuildLaunchArgs_ConservativeMode_ShouldAlwaysDisableSandbox()
    {
        var args = BrowserEngine.BuildLaunchArgs(new BrowserOptions { AntiDetection = false });

        args.Should().Contain("--no-sandbox", "保守档以兼容性优先（容器 / 受限沙箱环境）");
        args.Should().Contain("--disable-gpu");
    }

    [Fact]
    public void InstallHint_ShouldBePlatformSpecific()
    {
        var hint = BrowserEngine.InstallHint();

        hint.Should().NotBeNullOrWhiteSpace();

        if (OperatingSystem.IsLinux())
        {
            hint.Should().Contain("chromium", "Ubuntu 上要给出可执行的安装命令，而不是「请装 Chrome」");
        }
        else if (OperatingSystem.IsWindows())
        {
            hint.Should().Contain("Chrome");
        }
    }

    [Fact]
    public void MissingDisplay_OnWindows_ShouldBeFalse()
    {
        // Windows 没有 DISPLAY 变量；按环境变量判断会误杀所有 Windows 有头会话
        if (OperatingSystem.IsWindows())
            BrowserEngine.MissingDisplay().Should().BeFalse();
    }

    [Fact]
    public void MissingDisplay_OnLinuxWithoutDisplay_ShouldBeTrue()
    {
        // Linux 无 DISPLAY / WAYLAND_DISPLAY 时开有头必失败（Missing X server or $DISPLAY）。
        // 这里只在本机确实没有这两个变量时断言 —— 装了桌面环境的开发机不适用。
        if (!OperatingSystem.IsLinux())
            return;

        var hasDisplay = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY"))
                         || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

        if (!hasDisplay)
            BrowserEngine.MissingDisplay().Should().BeTrue();
    }
}
