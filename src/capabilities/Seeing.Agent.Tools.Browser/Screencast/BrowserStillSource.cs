using System.Text.Json;
using Microsoft.Extensions.Logging;
using PuppeteerSharp;

namespace Seeing.Agent.Tools.Browser;

/// <summary>
/// 静帧来源：按需抓一张「当前视口」的 JPEG（操作后补图、超采样）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要它</b>：screencast 只在页面有变化时推帧，且出图受 <c>maxWidth/maxHeight</c>
/// 限制。用户滚动到底部停下后，流可能停在半张旧图上；超采样模式下流的帧是降采样的。
/// 静帧是「此刻定格」的唯一来源。
/// </para>
/// <para>
/// <b>为什么先走 Puppeteer 的 screenshot</b>：它是官方路径，处理了视口裁剪、设备像素比、
/// 截图期间的临时视口覆盖等细节，正常情况一次往返就够。
/// </para>
/// <para>
/// <b>为什么还要一条 CDP 退路</b>：<c>ScreenshotDataAsync</c> 会调用
/// <c>Page.getLayoutMetrics</c> 并按整页/视口自行挑参数；在固定视口 + 有头窗口下
/// 偶尔返回的是整页或与视口不一致的尺寸（高 DPI 下尤其）。退路直接读
/// <c>cssVisualViewport</c> 的 pageX/pageY/clientWidth/clientHeight，把「当前可见区域」
/// 显式交给 <c>Page.captureScreenshot</c>，不依赖上层推断。
/// </para>
/// <para>
/// <b>为什么总预算 800ms 且超时就放弃</b>：静帧是锦上添花——前端在 2 秒后会自动重请求。
/// 为它排队等待会让画面流与工具调用一起卡住，代价远大于收益。
/// </para>
/// </remarks>
internal static class BrowserStillSource
{
    /// <summary>静帧质量。比流帧（80）高：静帧存在的意义就是看清文字。</summary>
    private const int JpegQuality = 90;

    /// <summary>单次抓图总预算（主路径 + 退路合计）。</summary>
    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromMilliseconds(800);

    /// <summary>
    /// 抓一张当前视口静帧；失败、超时或页面已关都返回 <see langword="null"/>。
    /// </summary>
    /// <param name="page">会话页面。</param>
    /// <param name="epoch">当前画面代次（写进帧头，前端据此丢弃旧代次）。</param>
    /// <param name="rev">代次内的版本号；按协议写进 <see cref="BrowserFrame.Seq"/>。</param>
    /// <param name="cancellationToken">取消令牌（连接断开 / 页面关闭）。</param>
    /// <param name="logger">
    /// 日志（可空）。静帧失败是常态级事件，只在 Debug 记录阶段名与异常：
    /// 没有日志时「偶尔抓不到图」完全无法定位（分不清是主路径挂了、退路挂了，还是超时）。
    /// </param>
    public static async Task<BrowserFrame?> CaptureAsync(
        BrowserPageHandle page,
        uint epoch,
        long rev,
        CancellationToken cancellationToken = default,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(page);

        var (width, height) = ResolveSize(page);

        var data = await CaptureWithBudgetAsync(page, cancellationToken, logger).ConfigureAwait(false);
        if (data is null || data.Length == 0)
            return null;

        return new BrowserFrame(data, rev, width, height, BrowserFrameKind.Still, epoch);
    }

    /// <summary>
    /// 静帧头里报告的宽高：视口 × 像素比（物理像素），并夹在帧头可表达的范围内。
    /// </summary>
    /// <remarks>
    /// 下限与 <see cref="BrowserPageHandle"/> 的视口约束保持一致（宽 ≥ 320、高 ≥ 240）：
    /// 静帧头里的宽高是给前端 sizing 用的，报一个比真实图更大的值会把画面拉伸。
    /// </remarks>
    internal static (int Width, int Height) ResolveSize(BrowserPageHandle page)
    {
        ArgumentNullException.ThrowIfNull(page);

        var pixelRatio = page.PixelRatio <= 0 ? 1 : (double)page.PixelRatio;
        var width = (int)Math.Round(page.ViewportWidth * pixelRatio);
        var height = (int)Math.Round(page.ViewportHeight * pixelRatio);

        return (Math.Clamp(width, 320, 4096), Math.Clamp(height, 240, 4096));
    }

    private static async Task<byte[]?> CaptureWithBudgetAsync(
        BrowserPageHandle page,
        CancellationToken cancellationToken,
        ILogger? logger)
    {
        // 不能只依赖 CancellationToken：ScreenshotDataAsync 没有 token 参数，
        // 卡住时只能「不再等它」，并把迟到的异常 observe 掉。
        var work = CaptureCoreAsync(page, logger);

        var finished = await Task.WhenAny(work, Task.Delay(CaptureTimeout, cancellationToken))
            .ConfigureAwait(false);

        if (!ReferenceEquals(finished, work))
        {
            logger?.LogDebug(
                "静帧抓取超预算（{Timeout}ms），放弃：{SessionId}",
                CaptureTimeout.TotalMilliseconds,
                page.SessionId);

            _ = work.ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            return null;
        }

        try
        {
            return await work.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "静帧抓取失败（忽略）：{SessionId}", page.SessionId);
            return null;
        }
    }

    private static async Task<byte[]?> CaptureCoreAsync(BrowserPageHandle page, ILogger? logger)
    {
        var data = await TryScreenshotAsync(page, logger).ConfigureAwait(false);
        if (data is not null)
            return data;

        return await TryCdpCaptureAsync(page, logger).ConfigureAwait(false);
    }

    /// <summary>
    /// 主路径：Puppeteer 的视口截图。
    /// </summary>
    /// <remarks>
    /// <b>为什么不走 <see cref="BrowserPageHandle.ScreenshotAsync"/></b>：那条路径会点亮
    /// 「智能体正在操作」遮罩并抢页级互斥。静帧是用户滚一下就触发的前端动作，
    /// 每滚一次闪一次遮罩（还可能挡住输入）显然不对。
    /// </remarks>
    private static async Task<byte[]?> TryScreenshotAsync(BrowserPageHandle page, ILogger? logger)
    {
        try
        {
            var data = await page.Page.ScreenshotDataAsync(new ScreenshotOptions
            {
                Type = ScreenshotType.Jpeg,
                Quality = JpegQuality,
                FullPage = false
            }).ConfigureAwait(false);

            return data is { Length: > 0 } ? data : null;
        }
        catch (Exception ex)
        {
            // 阶段名进日志：否则「静帧没了」只能靠猜是主路径还是退路出的问题。
            // 失败是常态（页面正在导航、有头窗口被遮挡），所以是 Debug 而非 Warning。
            logger?.LogDebug(ex, "静帧主路径（Puppeteer screenshot）失败，降级到 CDP：{SessionId}", page.SessionId);
            return null;
        }
    }

    /// <summary>
    /// 退路：独立 CDP 会话，按 <c>cssVisualViewport</c> 显式裁剪后截图。
    /// </summary>
    /// <remarks>
    /// 用独立会话而不是流式 screencast 那条：那条被 <c>startScreencast</c> 占着，
    /// 混发截图命令会与帧回调抢同一条连接，且 detach 时会连带打断画面流。
    /// </remarks>
    private static async Task<byte[]?> TryCdpCaptureAsync(BrowserPageHandle page, ILogger? logger)
    {
        ICDPSession? cdp = null;
        try
        {
            cdp = await page.Page.CreateCDPSessionAsync().ConfigureAwait(false);

            var metrics = await cdp.SendAsync("Page.getLayoutMetrics").ConfigureAwait(false);
            if (metrics is not { } metricsElement
                || !TryBuildClip(metricsElement, page.PixelRatio, out var clip))
            {
                logger?.LogDebug("静帧退路：getLayoutMetrics 没有可用的可见视口，放弃：{SessionId}", page.SessionId);
                return null;
            }

            var response = await cdp.SendAsync("Page.captureScreenshot", new Dictionary<string, object>
            {
                ["format"] = "jpeg",
                ["quality"] = JpegQuality,
                ["fromSurface"] = true,
                ["captureBeyondViewport"] = false,
                ["clip"] = clip
            }).ConfigureAwait(false);

            var data = response is { } responseElement ? ReadScreenshotData(responseElement) : null;
            if (data is null)
                logger?.LogDebug("静帧退路：captureScreenshot 未返回可用数据：{SessionId}", page.SessionId);

            return data;
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "静帧退路（CDP captureScreenshot）失败：{SessionId}", page.SessionId);
            return null;
        }
        finally
        {
            if (cdp is not null)
            {
                try
                {
                    await cdp.DetachAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // 页面可能已关闭
                    logger?.LogDebug(ex, "静帧退路：CDP 会话 detach 失败（忽略）：{SessionId}", page.SessionId);
                }
            }
        }
    }

    /// <summary>从 <c>getLayoutMetrics</c> 的结果构造「当前可见区域」裁剪参数。</summary>
    private static bool TryBuildClip(JsonElement metrics, double pixelRatio, out Dictionary<string, object> clip)
    {
        clip = [];

        // 新版 CDP 用 css* 前缀（CSS 像素），旧版没有前缀。优先新的：
        // 裁剪坐标必须是 CSS 像素，混用会导致高 DPI 下裁到页面中间。
        if (!metrics.TryGetProperty("cssVisualViewport", out var viewport)
            && !metrics.TryGetProperty("visualViewport", out viewport))
        {
            return false;
        }

        var x = ReadNumber(viewport, "pageX");
        var y = ReadNumber(viewport, "pageY");
        var width = ReadNumber(viewport, "clientWidth");
        var height = ReadNumber(viewport, "clientHeight");

        if (width <= 0 || height <= 0)
            return false;

        clip = new Dictionary<string, object>
        {
            ["x"] = x,
            ["y"] = y,
            ["width"] = width,
            ["height"] = height,
            ["scale"] = pixelRatio <= 0 ? 1d : pixelRatio
        };

        return true;
    }

    private static double ReadNumber(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : 0;

    private static byte[]? ReadScreenshotData(JsonElement response)
    {
        if (!response.TryGetProperty("data", out var dataElement)
            || dataElement.ValueKind != JsonValueKind.String
            || dataElement.GetString() is not { Length: > 0 } base64)
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
