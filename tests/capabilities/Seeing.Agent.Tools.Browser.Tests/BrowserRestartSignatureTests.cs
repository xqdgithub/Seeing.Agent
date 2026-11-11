using FluentAssertions;
using Seeing.Agent.Tools.Browser;
using Xunit;

namespace Seeing.Agent.Tools.Browser.Tests;

/// <summary>
/// 热重载守门：哪些配置改动需要重启浏览器，哪些即时生效。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么必须钉住</b>：`Headless`、`AntiDetection` 这类项只能在进程启动时生效。
/// 早期只比对 profile 目录，于是「在设置页关掉无头模式并保存」看似成功
/// （配置读到了新值、没有报错），但浏览器进程仍是旧的 —— 开关形同虚设。
/// 症状极难排查：用户以为配置没保存，实际是「保存了但没重启」。
/// </para>
/// <para>
/// <b>签名机制的价值</b>：把「所有启动时生效的项」拼成一个串。
/// 新增启动参数时只需加进签名，不必再想「还有一个地方要比较」。
/// 下面用参数化用例钉住「哪些项进签名」这条边界。
/// </para>
/// </remarks>
public class BrowserRestartSignatureTests
{
    [Theory]
    [InlineData("Headless")]
    [InlineData("AntiDetection")]
    [InlineData("ExecutablePath")]
    [InlineData("UserAgent")]
    [InlineData("ExtraArgs")]
    [InlineData("PersistLogin")]
    [InlineData("ProfileDirectory")]
    public void ProcessLevelSetting_ShouldChangeSignature(string setting)
    {
        var baseline = new BrowserOptions();
        var changed = new BrowserOptions();

        switch (setting)
        {
            case "Headless": changed.Headless = !baseline.Headless; break;
            case "AntiDetection": changed.AntiDetection = !baseline.AntiDetection; break;
            case "ExecutablePath": changed.ExecutablePath = @"C:\custom\edge.exe"; break;
            case "UserAgent": changed.UserAgent = "Mozilla/5.0 custom"; break;
            case "ExtraArgs": changed.ExtraArgs = ["--proxy-server=http://127.0.0.1:7890"]; break;
            case "PersistLogin": changed.PersistLogin = !baseline.PersistLogin; break;
            case "ProfileDirectory": changed.ProfileDirectory = Path.Combine(Path.GetTempPath(), "p-x"); break;
            default: throw new ArgumentOutOfRangeException(nameof(setting));
        }

        BrowserEngine.ComputeSignature(baseline)
            .Should().NotBe(BrowserEngine.ComputeSignature(changed),
                $"{setting} 只能在进程启动时生效，改动必须触发重启");
    }

    [Theory]
    [InlineData("IdleSeconds")]
    [InlineData("ViewportWidth")]
    [InlineData("ViewportHeight")]
    [InlineData("DefaultTimeoutSeconds")]
    [InlineData("AllowPrivateHosts")]
    public void RuntimeSetting_ShouldNotChangeSignature(string setting)
    {
        // 这些项在运行期实时读取（回收扫描 / 每次操作 / 每次导航），
        // 改动不需要重启；进签名会导致「改个超时就重启浏览器」的无谓抖动。
        var baseline = new BrowserOptions();
        var changed = new BrowserOptions();

        switch (setting)
        {
            case "IdleSeconds": changed.IdleSeconds = baseline.IdleSeconds + 60; break;
            case "ViewportWidth": changed.ViewportWidth = baseline.ViewportWidth + 100; break;
            case "ViewportHeight": changed.ViewportHeight = baseline.ViewportHeight + 100; break;
            case "DefaultTimeoutSeconds": changed.DefaultTimeoutSeconds = baseline.DefaultTimeoutSeconds + 10; break;
            case "AllowPrivateHosts": changed.AllowPrivateHosts = !baseline.AllowPrivateHosts; break;
            default: throw new ArgumentOutOfRangeException(nameof(setting));
        }

        BrowserEngine.ComputeSignature(baseline)
            .Should().Be(BrowserEngine.ComputeSignature(changed),
                $"{setting} 在运行期读取，改动不该触发浏览器重启");
    }

    [Fact]
    public void Signature_ShouldBeStableForEquivalentConfigs()
    {
        var a = new BrowserOptions { Headless = false, PersistLogin = false, IdleSeconds = 100 };
        var b = new BrowserOptions { Headless = false, PersistLogin = false, IdleSeconds = 999 };

        // IdleSeconds 不同但运行期项，签名必须相同 —— 否则配置面板每改一次都要重启
        BrowserEngine.ComputeSignature(a).Should().Be(BrowserEngine.ComputeSignature(b));
    }

    [Fact]
    public void Signature_ShouldDistinguishExtraArgsOrdering()
    {
        // 参数顺序会影响 Chromium 行为（后写覆盖前写），因此顺序必须进签名
        var a = new BrowserOptions { ExtraArgs = ["--a", "--b"] };
        var b = new BrowserOptions { ExtraArgs = ["--b", "--a"] };

        BrowserEngine.ComputeSignature(a).Should().NotBe(BrowserEngine.ComputeSignature(b));
    }

    [Fact]
    public void Signature_NullOptions_ShouldThrow()
    {
        var act = () => BrowserEngine.ComputeSignature(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
