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
}
