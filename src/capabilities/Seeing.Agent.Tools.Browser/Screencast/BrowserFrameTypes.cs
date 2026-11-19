using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PuppeteerSharp;

namespace Seeing.Agent.Tools.Browser;

/// <summary>一帧画面（JPEG）。</summary>
/// <param name="Data">JPEG 字节。</param>
/// <param name="Seq">帧序号（同代次内单调递增）。协议头中按 u32 传输。</param>
/// <param name="Width">画面宽（像素）。</param>
/// <param name="Height">画面高（像素）。</param>
/// <param name="Kind">帧种类。</param>
/// <param name="Epoch">浏览器会话代次：换页面或重启后递增，前端据此丢弃旧代次的帧。</param>
public sealed record BrowserFrame(
    byte[] Data,
    long Seq,
    int Width,
    int Height,
    BrowserFrameKind Kind = BrowserFrameKind.Flow,
    uint Epoch = 0)
{
    /// <summary>
    /// 逐字节比较 <see cref="Data"/>。
    /// </summary>
    /// <remarks>
    /// 编译器为 record 生成的相等性对数组字段用的是 <see cref="object.ReferenceEquals(object?, object?)"/>，
    /// 于是「内容完全一样的两帧」被判为不等——协议往返、去重、测试断言都会踩到，
    /// 而且这种不等是静默的（不会报错，只是永远 false）。这里改成值语义。
    /// </remarks>
    public bool Equals(BrowserFrame? other)
        => other is not null
           && Seq == other.Seq
           && Width == other.Width
           && Height == other.Height
           && Kind == other.Kind
           && Epoch == other.Epoch
           && Data.AsSpan().SequenceEqual(other.Data);

    /// <inheritdoc />
    public override int GetHashCode()
        => HashCode.Combine(Seq, Width, Height, Kind, Epoch, Data.Length);
}

/// <summary>画面流的观看者：宿主每收到一帧就调 <see cref="OnFrame"/>。</summary>
public interface IBrowserFrameSink
{
    /// <summary>推送一帧（实现方必须快速返回：不要在此阻塞）。</summary>
    void OnFrame(BrowserFrame frame);

    /// <summary>页面状态变化（地址/标题/代次）。</summary>
    void OnState(BrowserPageState state);

    /// <summary>出错（如浏览器被关闭）。</summary>
    void OnError(string message);
}

/// <summary>页面状态快照（推给前端状态条）。</summary>
/// <param name="Url">地址。</param>
/// <param name="Title">标题。</param>
/// <param name="RefEpoch">导航代次。</param>
/// <param name="Busy">是否有操作正在执行。</param>
/// <param name="Running">浏览器页面是否存活。</param>
/// <param name="Epoch">
/// 画面代次。与帧头里的 <see cref="BrowserFrame.Epoch"/> 同源：换页面或重启 screencast 后递增，
/// 前端据此丢弃旧代次的帧（否则重建后的第一帧会被上一代次的画面盖住）。
/// <para>
/// <b><c>0</c> 表示「未知」</b>（本会话从未有过画面流），不是「第 0 代」。
/// 前端不得把 0 当权威代次参与比较：收到 <c>epoch = 0</c> 时应保留上一次已知代次。
/// </para>
/// </param>
/// <param name="ViewportWidth">
/// 实际 <c>Page.setViewport</c> 的视口宽（CSS 像素）。<b>这是坐标与画布 backing 的唯一权威</b>：
/// 它就是面板宽，与出图倍率无关。
/// </param>
/// <param name="ViewportHeight">实际视口高（CSS 像素）。</param>
/// <param name="FrameScale">
/// 帧像素 / 视口 CSS。前端 <c>canvas.width = round(ViewportWidth × FrameScale)</c>。
/// 默认 = dpr（帧是物理像素，与画布 backing 1:1）；<c>Browser.StreamPixelRatio</c> 可改。
/// </param>
public sealed record BrowserPageState(
    string Url,
    string Title,
    int RefEpoch,
    bool Busy,
    bool Running,
    uint Epoch = 0,
    int ViewportWidth = 0,
    int ViewportHeight = 0,
    double FrameScale = 1);
