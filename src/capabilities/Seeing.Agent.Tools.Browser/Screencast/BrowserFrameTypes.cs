using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PuppeteerSharp;

namespace Seeing.Agent.Tools.Browser;

/// <summary>一帧画面（JPEG）。</summary>
/// <param name="Data">JPEG 字节。</param>
/// <param name="Seq">单调递增帧序号（诊断与丢帧判定用）。</param>
/// <param name="Width">画面宽（像素）。</param>
/// <param name="Height">画面高（像素）。</param>
public sealed record BrowserFrame(byte[] Data, long Seq, int Width, int Height);

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
