using FluentAssertions;
using Seeing.Agent.Tools.Browser;
using Xunit;

namespace Seeing.Agent.Tools.Browser.Tests;

/// <summary>
/// 持久登录配置守门。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要专门一条</b>：<c>PersistLogin</c> 决定的不只是一个开关，而是
/// 「用 <c>DefaultContext</c>（cookie 落盘、跨会话共享）还是每会话隔离上下文（cookie 不落盘）」
/// 这条语义分叉。选错任一侧都会静默失效：
/// </para>
/// <list type="bullet">
/// <item>该落盘却用了隔离上下文 → 用户登录后重启又变访客，且没有任何报错；</item>
/// <item>该隔离却用了默认上下文 → 两个会话的账号互相顶掉，也不报错。</item>
/// </list>
/// <para>
/// 实测依据：隔离上下文种下 cookie → 关浏览器 → 重开 → 0 个；
/// 默认上下文同样的流程 → cookie 仍在。
/// </para>
/// </remarks>
public class BrowserPersistLoginTests
{
    [Fact]
    public void PersistLogin_ShouldDefaultToTrue()
    {
        new BrowserOptions().PersistLogin.Should().BeTrue(
            "浏览器自动化的核心价值是复用已有登录态；默认关闭等于每次都要重新登录");
    }

    [Fact]
    public void ProfileDirectory_ShouldDefaultToNull()
    {
        new BrowserOptions().ProfileDirectory.Should().BeNull(
            "未配置时必须由引擎给出固定默认位置，而不是靠调用方拼路径");
    }

    [Fact]
    public void ResolveProfileDirectory_ShouldFallBackToUserSeeingFolder()
    {
        var resolved = BrowserEngine.ResolveProfileDirectory(new BrowserOptions());

        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".seeing",
            "browser-profile");

        resolved.Should().Be(expected);
        Path.IsPathRooted(resolved).Should().BeTrue("必须是绝对路径，否则相对当前工作目录漂移");
    }

    [Fact]
    public void ResolveProfileDirectory_ShouldPreferConfiguredPath()
    {
        var configured = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".seeing",
            "profiles",
            "work");

        BrowserEngine.ResolveProfileDirectory(new BrowserOptions { ProfileDirectory = configured })
            .Should().Be(configured);
    }

    [Fact]
    public void ResolveProfileDirectory_ShouldExpandRelativePath()
    {
        var resolved = BrowserEngine.ResolveProfileDirectory(
            new BrowserOptions { ProfileDirectory = "browser-profile-test" });

        Path.IsPathRooted(resolved).Should().BeTrue(
            "相对路径必须展开成绝对路径：否则热重载比对会因工作目录不同而误判 profile 变了");
    }

    [Fact]
    public void ResolveProfileDirectory_NullOptions_ShouldThrow()
    {
        var act = () => BrowserEngine.ResolveProfileDirectory(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ProfileChanged_ShouldBeDetectableViaResolvedPath()
    {
        // 热重载语义：配置换成另一个目录时，解析结果必须不同——
        // 相同的话引擎就不会重启浏览器，表现为「换了 profile 目录但登录态还是旧的」。
        var first = BrowserEngine.ResolveProfileDirectory(
            new BrowserOptions { ProfileDirectory = Path.Combine(Path.GetTempPath(), "p1") });
        var second = BrowserEngine.ResolveProfileDirectory(
            new BrowserOptions { ProfileDirectory = Path.Combine(Path.GetTempPath(), "p2") });

        first.Should().NotBe(second);
    }
}
