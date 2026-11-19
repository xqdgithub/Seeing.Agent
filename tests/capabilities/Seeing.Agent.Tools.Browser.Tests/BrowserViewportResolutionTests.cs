using FluentAssertions;
using Seeing.Agent.Tools.Browser;
using Xunit;

namespace Seeing.Agent.Tools.Browser.Tests;

/// <summary>
/// 视口换算守门：前端面板尺寸 + dpr 如何变成「实际 SetViewport 的 CSS 尺寸」与「帧倍率」。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么必须钉住</b>：实测（设计文档 1.3）screencast 永远按 <b>CSS</b> 视口出图，
/// 与 dsf 无关。因此普通模式下帧比画布 backing 小（画布按物理像素、靠静帧补清晰度），
/// 倍率必须如实报给前端；超采样模式则把 dpr 乘进视口、dsf=1，帧与视口同尺寸。
/// 两种模式的换算写错，前端的 backing 与坐标换算就会系统性偏掉（画面拉伸 / 点击错位）。
/// </para>
/// <para>
/// <b>为什么 dpr 是小数</b>：125% 缩放的屏上 <c>devicePixelRatio = 1.25</c>。取整会让
/// dsf 与前端像素比不符，文字比例失真 —— 这正是本项要修的问题。
/// </para>
/// </remarks>
public class BrowserViewportResolutionTests
{
    [Theory]
    [InlineData(800, 600, 1.25, false, 800, 600, 1.25)]
    [InlineData(800, 600, 1.25, true, 1000, 750, 1.0)]
    [InlineData(800, 600, 1.0, false, 800, 600, 1.0)]
    public void ResolveViewport_ShouldHonorMode(
        int w, int h, double dpr, bool hr, int ew, int eh, double es)
    {
        var r = BrowserPageHandle.ResolveViewport(w, h, dpr, hr);

        r.ViewportWidth.Should().Be(ew);
        r.ViewportHeight.Should().Be(eh);
        r.FrameScale.Should().Be(es);
    }

    [Fact]
    public void ResolveViewport_ShouldClampToHeaderLimits()
    {
        // 帧头宽高是 u16、协议侧限制 4096：配置/面板尺寸写坏不能让视口越界。
        var tooLarge = BrowserPageHandle.ResolveViewport(9999, 9999, 1.0, false);
        tooLarge.ViewportWidth.Should().Be(4096);
        tooLarge.ViewportHeight.Should().Be(4096);

        var tooSmall = BrowserPageHandle.ResolveViewport(1, 1, 1.0, false);
        tooSmall.ViewportWidth.Should().Be(320);
        tooSmall.ViewportHeight.Should().Be(240);

        // 超采样把 dpr 乘进去之后同样要夹住
        var scaledOverflow = BrowserPageHandle.ResolveViewport(4000, 4000, 2.0, true);
        scaledOverflow.ViewportWidth.Should().Be(4096);
        scaledOverflow.ViewportHeight.Should().Be(4096);
    }

    [Theory]
    [InlineData(0.5, 1.0)]
    [InlineData(1.25, 1.25)]
    [InlineData(2.0, 2.0)]
    [InlineData(3.0, 2.0)]
    public void NormalizePixelRatio_ShouldClamp(double input, double expected)
    {
        // 0/负数会让 SetViewport 直接报错；>2 的超采样只徒增编码成本（前端也按 2 封顶）。
        BrowserPageHandle.NormalizePixelRatio(input).Should().Be(expected);
        BrowserPageHandle.NormalizePixelRatio(double.NaN).Should().Be(1.0);
    }
}
