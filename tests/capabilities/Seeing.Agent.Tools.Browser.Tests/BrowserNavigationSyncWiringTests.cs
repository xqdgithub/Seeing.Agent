using FluentAssertions;
using Seeing.Agent.Tools.Browser;
using System.Reflection;
using Xunit;

namespace Seeing.Agent.Tools.Browser.Tests;

/// <summary>
/// 导航同步守门：主框架一跳，工作台地址栏与画面流必须跟上。
/// </summary>
/// <remarks>
/// <para>
/// <b>真实事故</b>：在工作台里操作（点搜索、点链接）后，真实页面已跳转，
/// 工作台地址栏仍停在旧 URL。有头、无头都复现。
/// 根因是页面只在部分 API 收尾路径刷新身份，既不监听 <c>FrameNavigated</c>，
/// 也不向画面流端点推状态。
/// </para>
/// <para>
/// 形态守门：编译器看不出「漏订 FrameNavigated」；与忙碌态通道测试同类，靠源码钉住。
/// </para>
/// </remarks>
public class BrowserNavigationSyncWiringTests
{
    [Fact]
    public void BrowserPageHandle_ShouldExposeNavigatedEvent()
    {
        typeof(BrowserPageHandle)
            .GetEvent(nameof(BrowserPageHandle.Navigated), BindingFlags.Public | BindingFlags.Instance)
            .Should().NotBeNull(
                "画布点击/页面自跳不会可靠地走到我们的 Navigate 收尾，" +
                "必须有 Navigated 事件把身份变化推给画面流端点");
    }

    [Fact]
    public void BrowserPageHandle_ShouldSubscribeMainFrameNavigated()
    {
        var ctor = ReadBrowserPageHandleRegion("internal BrowserPageHandle(");
        ctor.Should().NotBeNull();
        ctor.Should().Contain("FrameNavigated",
            "建页时必须订阅 FrameNavigated，否则导航后 CurrentUrl 不会刷新");
        ctor.Should().Contain("OnFrameNavigated",
            "订阅必须落到主框架处理函数，避免 iframe 导航打爆事件");
        ctor.Should().Contain("Popup +=",
            "target=_blank / window.open 会另开标签；必须订阅 Popup 才能折回当前页");
    }

    [Fact]
    public void BrowserPageHandle_Dispose_ShouldUnsubscribePopup()
    {
        var dispose = ReadBrowserPageHandleRegion("public async ValueTask DisposeAsync(");
        dispose.Should().NotBeNull();
        dispose.Should().Contain("Popup -=",
            "不摘 Popup 监听会在长会话里累积，页面关了还往已释放的句柄折回");
    }

    [Fact]
    public void BrowserSessionHost_ShouldInstallSinglePagePolicyBeforeFirstNavigation()
    {
        var source = ReadHostSource();
        source.Should().NotBeNull();
        var install = source!.IndexOf("InstallSinglePagePolicyAsync", StringComparison.Ordinal);
        var navigate = source.IndexOf("handle.NavigateAsync", StringComparison.Ordinal);
        install.Should().BeGreaterThanOrEqualTo(0, "建页后必须安装单页策略，否则第一页的新标签无人拦截");
        navigate.Should().BeGreaterThan(install, "策略必须在首次导航之前安装，否则第一份文档没有脚本");
    }

    [Fact]
    public void MouseDown_ShouldNotBringTheBrowserWindowForward()
    {
        var method = ReadBrowserPageHandleRegion("public Task MouseDownAsync(");
        method.Should().NotBeNull();

        var next = method!.IndexOf("public Task MouseUpAsync(", StringComparison.Ordinal);
        var body = next < 0 ? method : method[..next];
        body.Should().NotContain("BringToFront",
            "按下时把有头窗口抢到前台，抬起会落到浏览器而不是画布，页面就一直停在按住");
    }

    [Fact]
    public void Stream_ShouldShowPageWhenViewerAttaches_AndReleasePointerWhenViewerLeaves()
    {
        var source = ReadEndpointSource();
        source.Should().NotBeNull();
        var subscribe = source!.IndexOf("SubscribeAsync", StringComparison.Ordinal);
        var show = source.IndexOf("ShowToUserAsync", StringComparison.Ordinal);
        show.Should().BeGreaterThan(subscribe, "观看者接上画面时再把标签放到前台，不能拖到点击里");
        source.Should().Contain("ReleasePointerAsync",
            "离开会话时必须补一次鼠标抬起，否则回到原会话后点击全部失效");
    }

    [Fact]
    public void BrowserPageHandle_Dispose_ShouldUnsubscribeFrameNavigated()
    {
        var dispose = ReadBrowserPageHandleRegion("public async ValueTask DisposeAsync(");
        dispose.Should().NotBeNull();
        dispose.Should().Contain("FrameNavigated -=",
            "不摘监听会在长会话里累积，页面关了还回调已释放的句柄");
    }

    [Fact]
    public void BrowserStreamEndpoints_ShouldPublishStateOnNavigated()
    {
        var source = ReadEndpointSource();
        source.Should().NotBeNull();
        source.Should().Contain(".Navigated +=",
            "端点必须订阅 Navigated：只订 BusyChanged 覆盖不了人工点击触发的导航");
        source.Should().Contain("PublishState",
            "导航后必须推状态，否则地址栏停在旧 URL");

        // 证据探针已证明导航后不 Restart 仍出帧；禁止把强制 Restart 再加回来当「画面同步」
        var onNavigated = ExtractOnNavigatedHandler(source!);
        onNavigated.Should().NotBeNull();
        onNavigated.Should().NotContain("RestartAsync",
            "Navigated 处理里不得强制 RestartAsync（见 BrowserEvidenceProbes）");
    }

    private static string? ExtractOnNavigatedHandler(string source)
    {
        const string marker = "void OnNavigated(";
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            return null;

        // 取到下一个顶层 void/async Task 成员或 AttachBusy 为止
        var slice = source.AsSpan(start);
        var end = slice.IndexOf("void AttachBusy(".AsSpan(), StringComparison.Ordinal);
        return end < 0 ? slice.ToString() : slice[..end].ToString();
    }

    private static string? ReadBrowserPageHandleRegion(string marker)
    {
        var path = Path.Combine(
            FindRepositoryRoot(),
            "src", "capabilities", "Seeing.Agent.Tools.Browser", "Engine", "BrowserPageHandle.cs");

        if (!File.Exists(path))
            return null;

        var text = File.ReadAllText(path);
        var start = text.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            return null;

        // 取标记后一段足够覆盖构造/Dispose 主体
        var length = Math.Min(2500, text.Length - start);
        return text.Substring(start, length);
    }

    private static string? ReadHostSource()
    {
        var path = Path.Combine(
            FindRepositoryRoot(),
            "src", "capabilities", "Seeing.Agent.Tools.Browser", "Engine", "BrowserSessionHost.cs");

        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private static string? ReadEndpointSource()
    {
        var path = Path.Combine(
            FindRepositoryRoot(),
            "samples", "Seeing.Agent.WebUI", "Endpoints", "BrowserStreamEndpoints.cs");

        return File.Exists(path) ? File.ReadAllText(path) : null;
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

        throw new InvalidOperationException("未找到仓库根目录（Seeing.Agent.slnx）");
    }

    private static string CurrentFile(
        [System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
}
