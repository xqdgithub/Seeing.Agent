using PuppeteerSharp;

namespace Seeing.Agent.Tools.Browser;

/// <summary>
/// 建页策略的唯一出口：每个页面一律建成<b>独立窗口</b>（<see cref="CreatePageType.Window"/>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不能建标签页</b>：Chromium 只给「当前可见」的标签页出 screencast 帧。
/// 实测 <c>NewPageAsync()</c>（默认 <see cref="CreatePageType.Tab"/>）建出的后台标签页
/// 帧数直接掉到 <b>0</b>，切到前台的那一路才恢复 —— 多实例共享同一个浏览器时，
/// 表现为「只有一个工作台有画面，其它永远黑屏」。
/// </para>
/// <para>
/// 改成每页一个窗口后两路各约 60fps，与焦点无关（见 <c>BrowserMultiWindowFrameRateTests</c>）。
/// </para>
/// <para>
/// <b>语义代价（要承认）</b>：有头模式下每个会话会打开一个独立的 OS 窗口，不再是标签页。
/// 默认无头时用户看不到这个差异。
/// </para>
/// </remarks>
public static class BrowserPageFactory
{
    /// <summary>
    /// 建页参数：强制窗口类型。
    /// </summary>
    /// <remarks>
    /// 每次返回新实例：共享可变对象会让调用方的改动（例如将来补 <c>WindowBounds</c>）串场到其它页面。
    /// </remarks>
    public static CreatePageOptions Options() => new() { Type = CreatePageType.Window };

    /// <summary>
    /// 建一个页面。
    /// </summary>
    /// <param name="browser">浏览器实例（<paramref name="context"/> 为 null 时用它的默认上下文）。</param>
    /// <param name="context">
    /// 隔离上下文；<see langword="null"/> 表示建在 <c>DefaultContext</c>（持久登录模式必须走这条，
    /// 见 <c>BrowserOptions.PersistLogin</c> 的注释）。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static Task<IPage> CreateAsync(
        IBrowser browser,
        IBrowserContext? context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(browser);

        // PuppeteerSharp 的建页 API 不接受取消令牌；在这里先判一次，
        // 避免「已取消的调用仍然真的建出页面」。
        cancellationToken.ThrowIfCancellationRequested();

        return context is null
            ? browser.NewPageAsync(Options())
            : context.NewPageAsync(Options());
    }
}
