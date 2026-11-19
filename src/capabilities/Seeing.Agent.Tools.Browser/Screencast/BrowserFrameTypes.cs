using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PuppeteerSharp;

namespace Seeing.Agent.Tools.Browser;

/// <summary>一帧画面（JPEG）。</summary>
/// <param name="Data">JPEG 字节。</param>
/// <param name="Seq">流帧的单调递增帧序号；静帧（<see cref="BrowserFrameKind.Still"/>）时承载该代次内的 <c>rev</c>。协议头中按 u32 传输。</param>
/// <param name="Width">画面宽（像素）。</param>
/// <param name="Height">画面高（像素）。</param>
/// <param name="Kind">帧种类（流帧 / 静帧）。</param>
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
public sealed record BrowserPageState(string Url, string Title, int RefEpoch, bool Busy, bool Running);
