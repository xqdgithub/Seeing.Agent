using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Sessions;
using Seeing.Agent.Abstractions.Tools;
using System.Text.Json;

namespace Seeing.Agent.Tools.Browser.Tools;

/// <summary>
/// <c>browser_screenshot</c>：截图并把图片作为内容段交给模型（多模态）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么它不可被 <c>browser_snapshot</c> 取代</b>：快照是文本，精确且便宜，但看不到像素事实 ——
/// 图表形状、布局崩坏、验证码、图片内容。两者并存，让模型自己选。
/// </para>
/// <para>
/// <b>为什么落盘而不内联 base64</b>：会话文件是整文件重写；把 base64 写进会话会造成写放大
/// （与图片大小无关，每次工具状态迁移都重写一遍）。这里只写 <c>LocalPath</c>，
/// 由框架在出站前物化（见 <c>docs/superpowers/specs/2026-11-11-tool-image-parts-design.md</c>）。
/// </para>
/// </remarks>
public sealed class BrowserScreenshotTool : BrowserToolBase
{
    private readonly ISessionArtifactStore _artifacts;

    /// <summary>创建工具。</summary>
    /// <param name="logger">日志。</param>
    /// <param name="host">会话页面宿主。</param>
    /// <param name="artifacts">会话资源目录端口（截图落盘）。</param>
    public BrowserScreenshotTool(
        ILogger<BrowserScreenshotTool> logger,
        IBrowserSessionHost host,
        ISessionArtifactStore artifacts) : base(logger, host)
    {
        _artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
    }

    /// <inheritdoc />
    public override string Id => "browser_screenshot";

    /// <inheritdoc />
    public override string Description =>
        "截取网页画面并把图片交给模型查看（图表、布局、验证码等视觉信息用这个）。\n\n" +
        "- fullPage=true 截完整长页（可能很高）；默认只截可视区域。\n" +
        "- 给 ref 时只截该元素。\n" +
        "- 只读，不改变页面；读元素与文本用 `browser_snapshot`（更快、更省）。";

    /// <inheritdoc />
    public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            fullPage = new
            {
                type = "boolean",
                @default = false,
                description = "是否截取整页（默认 false 只截可视区域）"
            },
            @ref = new
            {
                type = "string",
                description = "只截该元素（可选，ref 由 browser_snapshot 获得）"
            }
        }
    });

    /// <inheritdoc />
    public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        if (!TryGetPage(context.SessionId, out var page, out var failure))
            return failure!;

        var fullPage = BrowserJson.ReadBool(arguments, "fullPage") ?? false;
        var reference = BrowserJson.ReadString(arguments, "ref");
        BrowserElementTarget? target = string.IsNullOrWhiteSpace(reference)
            ? null
            : new BrowserElementTarget { Ref = reference };

        try
        {
            var bytes = await page.ScreenshotAsync(fullPage, target, context.CancellationToken)
                .ConfigureAwait(false);

            var path = await _artifacts.SaveAsync(
                context.SessionId,
                "browser-screenshot",
                bytes,
                ".png",
                context.CancellationToken).ConfigureAwait(false);

            var title = string.IsNullOrWhiteSpace(page.CurrentTitle) ? "(无标题)" : page.CurrentTitle;
            var scope = target is { } t ? $"{t} 元素" : fullPage ? "整页" : "可视区域";

            return Success($"已截图（{scope}）：{title}（{page.CurrentUrl}）", context.SessionId,
                new Dictionary<string, object>
                {
                    ["url"] = page.CurrentUrl,
                    ["title"] = page.CurrentTitle,
                    ["bytes"] = bytes.Length,
                    ["full_page"] = fullPage,
                    ["target"] = target?.ToString() ?? "viewport"
                },
                [
                    new ToolOutputPart
                    {
                        Kind = ToolOutputPartKind.Image,
                        LocalPath = path,
                        MimeType = "image/png",
                        Name = "browser-screenshot.png"
                    }
                ]);
        }
        catch (TimeoutException ex)
        {
            // 落盘超时与「页面没响应」是两件事：笼统译成「截取页面超时：页面在限定时间内没有响应」
            // 会让模型以为页面慢，于是去重试导航/等待——方向完全错了。
            _logger.LogWarning(ex, "截图落盘超时（磁盘繁忙？）: {SessionId}", context.SessionId);
            return Failure(
                "截图已生成，但写入会话资源目录超时（磁盘繁忙）。" +
                "可改用 `browser_snapshot` 读取页面文本内容，或稍后重试");
        }
        catch (Exception ex)
        {
            return FailureFrom(ex, "截取页面");
        }
    }
}
