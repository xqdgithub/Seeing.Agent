using FluentAssertions;
using Seeing.Agent.Tools.Browser;
using Xunit;

namespace Seeing.Agent.Tools.Browser.Tests;

/// <summary>
/// 视口换算守门：前端面板尺寸 + dpr + 出图倍率 → 实际 SetViewport 的 CSS 尺寸与帧倍率。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么必须钉住</b>：视口永远是面板 CSS 尺寸（网页排版与真实窗口一致），
/// 帧像素 = 视口 × 帧倍率（默认 dpr，即物理像素）。两者写错，前端画布 backing
/// 与坐标换算就会系统性偏掉（画面拉伸 / 点击错位）。
/// </para>
/// <para>
/// <b>为什么 dpr 是小数</b>：125% 缩放的屏上 <c>devicePixelRatio = 1.25</c>。取整会让
/// dsf 与前端像素比不符，文字比例失真。
/// </para>
/// </remarks>
public class BrowserViewportResolutionTests
{
    [Theory]
    // 默认：出图倍率跟随 dpr（0 = 自动），视口始终等于面板尺寸
    [InlineData(800, 600, 1.25, 0.0, 800, 600, 1.25)]
    [InlineData(800, 600, 1.0, 0.0, 800, 600, 1.0)]
    [InlineData(800, 600, 2.0, 0.0, 800, 600, 2.0)]
    // 省带宽：固定按 CSS 像素出图
    [InlineData(800, 600, 1.25, 1.0, 800, 600, 1.0)]
    // 固定倍率
    [InlineData(800, 600, 1.25, 1.5, 800, 600, 1.5)]
    public void ResolveViewport_ShouldKeepLayoutAndScaleStream(
        int w, int h, double dpr, double streamRatio, int ew, int eh, double es)
    {
        var r = BrowserPageHandle.ResolveViewport(w, h, dpr, streamRatio);

        r.ViewportWidth.Should().Be(ew);
        r.ViewportHeight.Should().Be(eh);
        r.FrameScale.Should().Be(es);
    }

    [Fact]
    public void ResolveViewport_ShouldClampToHeaderLimits()
    {
        // 帧头宽高是 u16、协议侧限制 4096：配置/面板尺寸写坏不能让视口越界。
        var tooLarge = BrowserPageHandle.ResolveViewport(9999, 9999, 1.0, 0.0);
        tooLarge.ViewportWidth.Should().Be(4096);
        tooLarge.ViewportHeight.Should().Be(4096);

        var tooSmall = BrowserPageHandle.ResolveViewport(1, 1, 1.0, 0.0);
        tooSmall.ViewportWidth.Should().Be(320);
        tooSmall.ViewportHeight.Should().Be(240);

        // 倍率乘上去也不能让帧超过 4096（缩小倍率而不是缩视口，保证排版不变）
        var scaledOverflow = BrowserPageHandle.ResolveViewport(4000, 4000, 2.0, 0.0);
        scaledOverflow.ViewportWidth.Should().Be(4000);
        scaledOverflow.FrameScale.Should().BeApproximately(4096d / 4000d, 0.0001);
        (scaledOverflow.ViewportWidth * scaledOverflow.FrameScale).Should().BeApproximately(4096, 0.5);
    }

    [Theory]
    [InlineData(0.5, 1.0)]
    [InlineData(1.25, 1.25)]
    [InlineData(2.0, 2.0)]
    [InlineData(3.0, 2.0)]
    public void NormalizePixelRatio_ShouldClamp(double input, double expected)
    {
        // 0/负数会让 SetViewport 直接报错；>2 的倍率只徒增编码成本（前端也按 2 封顶）。
        BrowserPageHandle.NormalizePixelRatio(input).Should().Be(expected);
        BrowserPageHandle.NormalizePixelRatio(double.NaN).Should().Be(1.0);
    }

    [Theory]
    [InlineData(0.0, 0.0)]      // 0 = 跟随设备像素比
    [InlineData(-1.0, 0.0)]     // 负数等同「未设置」
    [InlineData(0.5, 1.0)]      // 比 CSS 还糊没有意义
    [InlineData(1.0, 1.0)]
    [InlineData(1.5, 1.5)]
    [InlineData(3.0, 2.0)]
    public void EffectiveStreamRatio_ShouldNormalize(double input, double expected)
    {
        new BrowserOptions { StreamPixelRatio = input }.EffectiveStreamRatio.Should().Be(expected);
    }
}
