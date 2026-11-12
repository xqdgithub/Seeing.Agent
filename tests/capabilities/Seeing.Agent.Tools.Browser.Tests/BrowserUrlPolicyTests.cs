using FluentAssertions;
using Seeing.Agent.Tools.Browser;
using Xunit;

namespace Seeing.Agent.Tools.Browser.Tests;

/// <summary>
/// 导航地址准入：这是 agent 能访问哪些地址的唯一闸口。
/// <para>
/// 钉住三件事：① 内网/回环默认拒绝；② 只放行 http/https/about:blank/data:text/html；
/// ③ 配置开关能放行内网（本地开发需要）。
/// </para>
/// </summary>
public class BrowserUrlPolicyTests
{
    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://example.com/path?q=1")]
    [InlineData("https://sub.example.com:8443/a")]
    public void Evaluate_PublicHttp_ShouldAllow(string url)
    {
        BrowserUrlPolicy.Evaluate(url, allowPrivateHosts: false).Allowed.Should().BeTrue();
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.3.4")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]   // 云元数据服务：最典型的 SSRF 目标
    [InlineData("0.0.0.0")]
    [InlineData("100.64.1.1")]
    // 尾点（绝对 FQDN 写法）：DNS 与浏览器会照常解析到回环，必须同样拒绝
    [InlineData("localhost.")]
    [InlineData("127.0.0.1.")]
    [InlineData("foo.localhost.")]
    [InlineData("LOCALHOST.")]
    public void IsPrivateHost_PrivateLiteral_ShouldBeTrue(string host)
    {
        BrowserUrlPolicy.IsPrivateHost(host).Should().BeTrue();
    }

    [Theory]
    [InlineData("http://localhost./admin")]
    [InlineData("http://127.0.0.1./")]
    [InlineData("http://foo.localhost./")]
    public void Evaluate_TrailingDotLoopback_ShouldDeny(string url)
    {
        // 实测过的绕过：Uri.Host 保留末尾点，而 IPAddress.TryParse("127.0.0.1.") 失败，
        // 于是「localhost.」「127.0.0.1.」都能在 AllowPrivateHosts=false 时访问本机任意端口。
        var verdict = BrowserUrlPolicy.Evaluate(url, allowPrivateHosts: false);

        verdict.Allowed.Should().BeFalse($"{url} 实际指向回环，必须拒绝");
        verdict.Reason.Should().Contain("内网");
    }

    [Theory]
    [InlineData("example.com")]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    public void IsPrivateHost_PublicHost_ShouldBeFalse(string host)
    {
        BrowserUrlPolicy.IsPrivateHost(host).Should().BeFalse();
    }

    [Fact]
    public void Evaluate_PrivateHost_ShouldDenyWithActionableReason()
    {
        var verdict = BrowserUrlPolicy.Evaluate("http://192.168.1.10/admin", allowPrivateHosts: false);

        verdict.Allowed.Should().BeFalse();
        verdict.Reason.Should().Contain("内网");
        verdict.Reason.Should().Contain("Browser.AllowPrivateHosts");
    }

    [Fact]
    public void Evaluate_PrivateHost_WithOptIn_ShouldAllow()
    {
        BrowserUrlPolicy.Evaluate("http://127.0.0.1:3000", allowPrivateHosts: true).Allowed.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_AboutBlank_ShouldAllow()
    {
        BrowserUrlPolicy.Evaluate("about:blank", allowPrivateHosts: false).Allowed.Should().BeTrue();
    }

    [Theory]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://example.com/x")]
    public void Evaluate_UnsupportedScheme_ShouldDeny(string url)
    {
        var verdict = BrowserUrlPolicy.Evaluate(url, allowPrivateHosts: false);

        verdict.Allowed.Should().BeFalse();
        verdict.Reason.Should().Contain("协议");
    }

    [Fact]
    public void Evaluate_DataTextHtml_ShouldAllow()
    {
        BrowserUrlPolicy.Evaluate("data:text/html,<h1>hi</h1>", allowPrivateHosts: false).Allowed.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_DataImage_ShouldDeny()
    {
        var verdict = BrowserUrlPolicy.Evaluate("data:image/png;base64,AAAA", allowPrivateHosts: false);

        verdict.Allowed.Should().BeFalse();
        verdict.Reason.Should().Contain("data:text/html");
    }

    [Fact]
    public void Evaluate_OversizedDataUrl_ShouldDeny()
    {
        var url = "data:text/html," + new string('x', BrowserUrlPolicy.MaxDataUrlLength);

        var verdict = BrowserUrlPolicy.Evaluate(url, allowPrivateHosts: false);

        verdict.Allowed.Should().BeFalse();
        verdict.Reason.Should().Contain("过大");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Evaluate_Empty_ShouldDeny(string? url)
    {
        BrowserUrlPolicy.Evaluate(url, allowPrivateHosts: false).Allowed.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_RelativePath_ShouldDeny()
    {
        var verdict = BrowserUrlPolicy.Evaluate("/just/a/path", allowPrivateHosts: false);

        verdict.Allowed.Should().BeFalse();
        verdict.Reason.Should().Contain("绝对地址");
    }
}

/// <summary>弹出页地址分类：空白页要继续等，其余跟地址栏同一套准入。</summary>
public class BrowserSingleTabPolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("about:blank")]
    [InlineData("about:blank#blocked")]
    public void Classify_Blank_ShouldStayPending(string? url)
    {
        BrowserSingleTabPolicy.Classify(url, allowPrivateHosts: false).Kind
            .Should().Be(PopupUrlKind.Pending);
    }

    [Fact]
    public void Classify_PublicHttp_ShouldAllow()
    {
        var result = BrowserSingleTabPolicy.Classify("https://example.com/a", allowPrivateHosts: false);

        result.Kind.Should().Be(PopupUrlKind.Allowed);
        result.Url.Should().Be("https://example.com/a");
    }

    [Fact]
    public void Classify_Loopback_ShouldDenyUnlessConfigured()
    {
        BrowserSingleTabPolicy.Classify("http://127.0.0.1/", allowPrivateHosts: false).Kind
            .Should().Be(PopupUrlKind.Denied);

        BrowserSingleTabPolicy.Classify("http://127.0.0.1/", allowPrivateHosts: true).Kind
            .Should().Be(PopupUrlKind.Allowed);
    }

    [Fact]
    public void Classify_JavascriptUrl_ShouldDeny()
    {
        BrowserSingleTabPolicy.Classify("javascript:alert(1)", allowPrivateHosts: true).Kind
            .Should().Be(PopupUrlKind.Denied);
    }
}
