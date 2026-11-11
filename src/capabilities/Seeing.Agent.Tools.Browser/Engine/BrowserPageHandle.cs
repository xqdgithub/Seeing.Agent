using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PuppeteerSharp;
using PuppeteerSharp.Input;

namespace Seeing.Agent.Tools.Browser;

/// <summary>
/// 单页能力封装：导航、快照、点击、输入、滚动、等待、截图、求值、前进后退。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不把 Puppeteer 类型暴露给工具</b>：工具只该读参数、调一个方法、产出 <c>ToolResult</c>。
/// 把 <see cref="IPage"/> 泄漏出去会让每个工具各自处理超时、dialog、ref 失效，行为必然漂移。
/// </para>
/// <para>
/// <b>为什么串行</b>：所有方法都在 <see cref="SemaphoreSlim"/> 内执行。同一页面上两个操作并发
/// 会互相打断（例如点击时另一次导航把元素带走），而 agent 的任务本身就是线性的。
/// </para>
/// </remarks>
public sealed class BrowserPageHandle : IAsyncDisposable
{
    /// <summary>
    /// 点击后最多等多久看它是否引发导航。
    /// </summary>
    /// <remarks>
    /// 太短会让「慢速导航」被漏判（模型立刻读旧 URL → 以为没反应）；
    /// 太长会让「纯前端交互的点击」白等。600ms 覆盖大多数同站跳转的首字节延迟，
    /// 真正的慢站点由后续 <c>browser_wait_for</c> 兜底。
    /// </remarks>
    private static readonly TimeSpan ClickNavigationWait = TimeSpan.FromMilliseconds(600);

    private readonly IPage _page;
    private readonly IOptionsMonitor<BrowserOptions> _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<string> _dialogs = [];

    /// <summary>最近的人工操作（环形缓冲，只保留最近 <see cref="MaxHumanActions"/> 条）。</summary>
    /// <remarks>
    /// 有上限是必要的：长时间会话里人工操作可能上千条，
    /// 无限累积会让摘要文本过长（塞进 agent 上下文时反而淹没重点）。
    /// </remarks>
    private readonly List<BrowserHumanAction> _humanActions = [];

    private const int MaxHumanActions = 50;

    private bool _disposed;
    private int _busy;

    /// <summary>
    /// 忙碌状态变化（进入 / 退出会话互斥时各触发一次）。
    /// </summary>
    /// <remarks>
    /// <b>为什么需要事件而不是让调用方"前后各推一次"</b>：<c>IsBusy</c> 是在
    /// <c>RunAsync</c> 内部置位与复位的，调用方在「操作前」读到的是 false、
    /// 「操作后」读到的也是 false —— 推出去的永远是「不忙」，
    /// 前端的「智能体正在操作」遮罩成了永不生效的死代码。
    /// 只有持锁的那一层自己发事件，时机才对。
    /// </remarks>
    public event Action<BrowserPageHandle>? BusyChanged;

    /// <summary>
    /// 是否正有一次操作在执行（仅 agent 的工具调用；人工输入不点亮）。
    /// </summary>
    public bool IsBusy => Volatile.Read(ref _busy) != 0;

    /// <summary>取最近的人工操作快照（从旧到新）。</summary>
    public IReadOnlyList<BrowserHumanAction> RecentHumanActions
    {
        get
        {
            lock (_humanActions)
                return _humanActions.ToArray();
        }
    }

    /// <summary>清空人工操作记录（接管流程在处理完后调用，避免重复告知）。</summary>
    public void ClearHumanActions()
    {
        lock (_humanActions)
            _humanActions.Clear();
    }

    /// <summary>
    /// 记一条人工操作（写审计日志 + 进环形缓冲）。
    /// </summary>
    /// <remarks>
    /// <b>为什么审计日志与缓冲都要有</b>：日志会被日志框架的级别/滚动策略丢弃或轮转，
    /// 缓冲则要在进程内被「告知 agent」的路径读取。两者目的不同，不能互相替代。
    /// </remarks>
    private void RecordHumanAction(string kind, string target, string? value = null)
    {
        var action = new BrowserHumanAction
        {
            At = DateTimeOffset.UtcNow,
            Kind = kind,
            Target = target,
            Value = value,
            Url = CurrentUrl
        };

        lock (_humanActions)
        {
            _humanActions.Add(action);
            if (_humanActions.Count > MaxHumanActions)
                _humanActions.RemoveAt(0);
        }

        _logger.LogInformation(
            "人工操作 {Kind} {Target} {Value}（会话 {SessionId}，页面 {Url}）",
            action.Kind,
            action.Target,
            action.Value ?? "-",
            SessionId,
            action.Url);
    }

    internal BrowserPageHandle(
        string sessionId,
        IPage page,
        int refEpoch,
        IOptionsMonitor<BrowserOptions> options,
        ILogger logger)
    {
        SessionId = sessionId;
        _page = page;
        RefEpoch = refEpoch;
        _options = options;
        _logger = logger;

        // 建页时用的就是配置视口（BrowserSessionHost 已 SetViewportAsync）
        ViewportWidth = options.CurrentValue.EffectiveWidth;
        ViewportHeight = options.CurrentValue.EffectiveHeight;
        // 建页时还不知道前端 dpr；首次 resize 消息会带上并触发 screencast 重启。
        PixelRatio = 1;

        _page.Dialog += OnDialog;
        _page.Console += OnConsole;
    }

    /// <summary>所属会话 id。</summary>
    public string SessionId { get; }

    /// <summary>
    /// 当前导航代次。每次导航 +1；<c>data-seeing-ref</c> 在该代次内才有效。
    /// </summary>
    public int RefEpoch { get; private set; }

    /// <summary>最后一次活动时间（UTC），供空闲回收判定。</summary>
    public DateTimeOffset LastActivityAt { get; private set; } = DateTimeOffset.UtcNow;

    /// <summary>最近一次已知地址。</summary>
    public string CurrentUrl { get; private set; } = "about:blank";

    /// <summary>最近一次已知标题。</summary>
    public string CurrentTitle { get; private set; } = string.Empty;

    /// <summary>
    /// 当前视口尺寸（CSS 像素）。
    /// </summary>
    /// <remarks>
    /// <b>为什么必须记录</b>：CDP <c>Page.startScreencast</c> 的 <c>maxWidth/maxHeight</c>
    /// 只在启动时生效，视口变化后必须重启 screencast 才能跟上。若重启时读的是<b>配置</b>视口而不是
    /// 实际视口，面板拖宽后帧仍按配置值等比缩小 → 画布被降采样、文字发虚
    /// （这正是「resize 后重启」要解决的问题，读错来源等于没修）。
    /// </remarks>
    public int ViewportWidth { get; private set; }

    /// <summary>当前视口高度（CSS 像素）。</summary>
    public int ViewportHeight { get; private set; }

    /// <summary>
    /// 出图像素比（前端 <c>devicePixelRatio</c>，上限 2）。
    /// </summary>
    /// <remarks>
    /// <b>为什么布局与出图要分开</b>：CDP 的 <c>Page.setViewport</c> 用 CSS 像素
    /// （决定网页怎么排版），而 screencast 的 <c>maxWidth/maxHeight</c> 决定编码分辨率。
    /// 高 DPI 屏上 CSS 554px 占 1108 个物理像素：若 screencast 仍按 554 编码，
    /// 浏览器会把帧放大 2 倍显示 → 文字发虚（用户报的「分辨率很低」）。
    /// 两者不能混用：把 dpr 乘进 viewport 会让网页以为自己在 2 倍宽的屏幕上（排版稀疏）。
    /// </remarks>
    public int PixelRatio { get; private set; } = 1;

    /// <summary>出图宽度（CSS 宽 × 像素比）。</summary>
    public int RenderWidth => Math.Clamp(ViewportWidth * PixelRatio, 320, 4096);

    /// <summary>出图高度（CSS 高 × 像素比）。</summary>
    public int RenderHeight => Math.Clamp(ViewportHeight * PixelRatio, 240, 4096);

    /// <summary>底层页面（仅供 screencast 等基础设施使用，工具不直接碰）。</summary>
    internal IPage Page => _page;

    /// <summary>取出并清空被自动关闭的对话框提示。</summary>
    /// <remarks>
    /// 必须持锁：<see cref="OnDialog"/> 在 CDP 回调线程上 <c>Add</c>，本方法在工具线程上
    /// <c>ToArray</c>/<c>Clear</c>。<c>List</c> 不是线程安全容器，并发会让内部数组扩容与快照交叠，
    /// 最坏直接从 <see cref="Tools.BrowserToolBase.Success"/> 里抛异常——把一次成功的点击变成失败。
    /// </remarks>
    public IReadOnlyList<string> DrainDialogs()
    {
        lock (_dialogs)
        {
            if (_dialogs.Count == 0)
                return Array.Empty<string>();

            var copy = _dialogs.ToArray();
            _dialogs.Clear();
            return copy;
        }
    }

    /// <summary>导航到指定地址（agent 工具路径：点亮「智能体正在操作」）。</summary>
    public Task<BrowserNavigationResult> NavigateAsync(
        string url,
        BrowserWaitUntil waitUntil,
        CancellationToken cancellationToken)
        => RunAsync(() => NavigateCoreAsync(url, waitUntil), cancellationToken);

    /// <summary>
    /// 导航到指定地址（<b>人工</b>路径：工作台地址栏，不点亮「智能体正在操作」）。
    /// </summary>
    /// <remarks>
    /// <b>为什么必须区分</b>：地址栏是用户的操作，而忙碌遮罩的语义是「agent 的工具正在操作页面」。
    /// 走 <see cref="RunAsync"/> 会在用户自己输入网址后点亮遮罩，并且因为遮罩**拦截输入**，
    /// 用户随即连自己刚打开的页面都点不动 —— 表现为「我输入网址后整块区域不能用了」。
    /// 与人工鼠标输入（<see cref="RunHumanAsync{T}"/>）同一条纪律。
    /// </remarks>
    public Task<BrowserNavigationResult> NavigateAsUserAsync(
        string url,
        BrowserWaitUntil waitUntil,
        CancellationToken cancellationToken)
        => RunHumanAsync(() => NavigateCoreAsync(url, waitUntil), cancellationToken);

    private async Task<BrowserNavigationResult> NavigateCoreAsync(string url, BrowserWaitUntil waitUntil)
    {
        var stopwatch = Stopwatch.StartNew();

        var response = await _page.GoToAsync(url, new NavigationOptions
        {
            WaitUntil = MapWaitUntil(waitUntil),
            Timeout = (int)ResolveTimeout().TotalMilliseconds
        }).ConfigureAwait(false);

        stopwatch.Stop();
        RefEpoch++;
        await RefreshIdentityAsync().ConfigureAwait(false);

        return new BrowserNavigationResult(
            CurrentUrl,
            CurrentTitle,
            response?.Status is { } status ? (int)status : null,
            stopwatch.ElapsedMilliseconds);
    }

    /// <summary>后退（agent 工具路径）。</summary>
    public Task<BrowserNavigationResult> BackAsync(BrowserWaitUntil waitUntil, CancellationToken cancellationToken)
        => RunAsync(() => BackCoreAsync(waitUntil), cancellationToken);

    /// <summary>后退（<b>人工</b>路径：工作台工具栏，不点亮「智能体正在操作」）。</summary>
    public Task<BrowserNavigationResult> BackAsUserAsync(BrowserWaitUntil waitUntil, CancellationToken cancellationToken)
        => RunHumanAsync(() => BackCoreAsync(waitUntil), cancellationToken);

    private async Task<BrowserNavigationResult> BackCoreAsync(BrowserWaitUntil waitUntil)
    {
        var stopwatch = Stopwatch.StartNew();
        var response = await _page.GoBackAsync(new NavigationOptions
        {
            WaitUntil = MapWaitUntil(waitUntil),
            Timeout = (int)ResolveTimeout().TotalMilliseconds
        }).ConfigureAwait(false);

        stopwatch.Stop();
        RefEpoch++;
        await RefreshIdentityAsync().ConfigureAwait(false);

        return new BrowserNavigationResult(
            CurrentUrl,
            CurrentTitle,
            response?.Status is { } status ? (int)status : null,
            stopwatch.ElapsedMilliseconds);
    }

    /// <summary>前进（agent 工具路径）。</summary>
    public Task<BrowserNavigationResult> ForwardAsync(BrowserWaitUntil waitUntil, CancellationToken cancellationToken)
        => RunAsync(() => ForwardCoreAsync(waitUntil), cancellationToken);

    /// <summary>前进（<b>人工</b>路径：工作台工具栏，不点亮「智能体正在操作」）。</summary>
    public Task<BrowserNavigationResult> ForwardAsUserAsync(BrowserWaitUntil waitUntil, CancellationToken cancellationToken)
        => RunHumanAsync(() => ForwardCoreAsync(waitUntil), cancellationToken);

    private async Task<BrowserNavigationResult> ForwardCoreAsync(BrowserWaitUntil waitUntil)
    {
        var stopwatch = Stopwatch.StartNew();
        var response = await _page.GoForwardAsync(new NavigationOptions
        {
            WaitUntil = MapWaitUntil(waitUntil),
            Timeout = (int)ResolveTimeout().TotalMilliseconds
        }).ConfigureAwait(false);

        stopwatch.Stop();
        RefEpoch++;
        await RefreshIdentityAsync().ConfigureAwait(false);

        return new BrowserNavigationResult(
            CurrentUrl,
            CurrentTitle,
            response?.Status is { } status ? (int)status : null,
            stopwatch.ElapsedMilliseconds);
    }

    /// <summary>重新加载（agent 工具路径）。</summary>
    public Task ReloadAsync(CancellationToken cancellationToken)
        => RunAsync(ReloadCoreAsync, cancellationToken);

    /// <summary>重新加载（<b>人工</b>路径：工作台工具栏刷新按钮，不点亮「智能体正在操作」）。</summary>
    public Task ReloadAsUserAsync(CancellationToken cancellationToken)
        => RunHumanAsync(ReloadCoreAsync, cancellationToken);

    private async Task<bool> ReloadCoreAsync()
    {
        await _page.ReloadAsync(new ReloadOptions
        {
            Timeout = (int)ResolveTimeout().TotalMilliseconds
        }).ConfigureAwait(false);

        RefEpoch++;
        await RefreshIdentityAsync().ConfigureAwait(false);
        return true;
    }

    /// <summary>采集快照。</summary>
    public Task<BrowserSnapshot> SnapshotAsync(
        int maxElements,
        bool includeText,
        CancellationToken cancellationToken)
        => RunAsync(async () =>
        {
            var script = BrowserSnapshotBuilder.BuildScript(maxElements, includeText);
            var payload = await _page.EvaluateFunctionAsync<JsonElement>(script).ConfigureAwait(false);

            var snapshot = BrowserSnapshotBuilder.Parse(payload, RefEpoch);

            // 采集脚本本身不改导航代次；但地址可能被 SPA 路由改过，顺手同步
            CurrentUrl = string.IsNullOrWhiteSpace(snapshot.Url) ? CurrentUrl : snapshot.Url;
            CurrentTitle = snapshot.Title;

            return snapshot;
        }, cancellationToken);

    /// <summary>点击。</summary>
    /// <remarks>
    /// <b>点击会导致导航时必须等它完成</b>：否则调用方紧接着读 URL/快照，拿到的还是旧页面，
    /// 于是模型以为「点了没反应」而反复重试（实测：Bing 分页点击后 URL 未变 → 死循环）。
    /// 这里用「点击 + 竞速等待导航事件」的方式：有导航就等它落到 DOMContentLoaded，
    /// 没有导航（纯前端交互）就立刻返回，不为无导航的点击白等。
    /// </remarks>
    public Task<string> ClickAsync(
        BrowserElementTarget target,
        MouseButton button,
        int clickCount,
        CancellationToken cancellationToken)
        => RunAsync<string>(async () =>
        {
            if (target.HasCoordinates)
            {
                var beforeCoordinates = _page.Url;
                var navigatedCoordinates = WaitForNavigationAsync();

                await _page.Mouse.ClickAsync(
                    (decimal)target.X!.Value,
                    (decimal)target.Y!.Value,
                    new ClickOptions { Button = button, Count = Math.Clamp(clickCount, 1, 3) })
                    .ConfigureAwait(false);

                await SettleAfterClickAsync(navigatedCoordinates, beforeCoordinates).ConfigureAwait(false);

                return $"已点击坐标 {target}";
            }

            var selector = target.ResolveSelector()!;
            var element = await _page.QuerySelectorAsync(selector).ConfigureAwait(false);

            if (element is null)
                throw BrowserElementNotFoundException.For(target, RefEpoch);

            var box = await element.BoundingBoxAsync().ConfigureAwait(false);
            if (box is null)
                throw new BrowserElementNotInteractableException($"{target} 当前不可点击（不可见或被遮挡）");

            // 用 element.ClickAsync 而不是「取 BoundingBox 再 Mouse.Click」：
            // BoundingBox 返回的是**文档坐标**，而 Mouse.ClickAsync 期望**视口坐标**。
            // 元素只要不在当前视口内（页面底部的分页按钮、长列表里的条目…），
            // 坐标点击就会落到视口外的空白处 —— **静默丢失、不报错**，工具还照样回「已点击」。
            // 实测：元素在 y=1540、视口高 400 时坐标点击完全无效；
            // element.ClickAsync 会先 scrollIntoViewIfNeeded 再点，工作正常。
            var beforeUrl = _page.Url;
            var navigated = WaitForNavigationAsync();

            await element.ClickAsync(new ClickOptions
            {
                Button = button,
                Count = Math.Clamp(clickCount, 1, 3)
            }).ConfigureAwait(false);

            await SettleAfterClickAsync(navigated, beforeUrl).ConfigureAwait(false);

            return $"已点击 {target}";
        }, cancellationToken);

    /// <summary>
    /// 点击后等待可能发生的导航（最多 <c>ClickNavigationWait</c>）。
    /// </summary>
    /// <remarks>
    /// 点击不一定触发导航（纯前端交互），所以不能无条件 `WaitForNavigation` —— 那会白等到超时。
    /// 这里把导航等待与一个短超时竞速：谁先到用谁，最后统一刷新地址/标题。
    /// </remarks>
    private async Task SettleAfterClickAsync(Task<bool> navigated, string beforeUrl)
    {
        var completed = await Task.WhenAny(navigated, Task.Delay(ClickNavigationWait)).ConfigureAwait(false);
        if (ReferenceEquals(completed, navigated))
        {
            try
            {
                await navigated.ConfigureAwait(false);
            }
            catch
            {
                // 导航被后续导航打断等：不影响「点击已发生」这一事实
            }
        }

        // 地址变了说明发生了导航：推进代次，让旧 ref 明确失效（模型必须重新快照）
        if (!string.Equals(_page.Url, beforeUrl, StringComparison.Ordinal))
            RefEpoch++;

        await RefreshIdentityAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 等一次主框架导航完成（DOMContentLoaded 或超时）。
    /// </summary>
    /// <returns>true = 确实发生了导航。</returns>
    private Task<bool> WaitForNavigationAsync()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnNavigated(object? sender, FrameNavigatedEventArgs e)
        {
            if (!ReferenceEquals(e.Frame, _page.MainFrame))
                return;

            tcs.TrySetResult(true);
        }

        EventHandler<FrameNavigatedEventArgs> handler = OnNavigated;
        _page.FrameNavigated += handler;

        // 无论是「导航发生」还是「超时未导航」，都要摘掉监听，否则长会话里会累积
        _ = tcs.Task.ContinueWith(
            _ => _page.FrameNavigated -= handler,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return tcs.Task;
    }

    /// <summary>输入文本。</summary>
    public Task<string> TypeAsync(
        BrowserElementTarget target,
        string text,
        bool clear,
        bool submit,
        int delayMs,
        CancellationToken cancellationToken)
        => RunAsync<string>(async () =>
        {
            var selector = target.ResolveSelector();
            if (selector is null)
                throw new BrowserElementNotFoundException("缺少元素定位：请提供 ref 或 selector");

            var element = await _page.QuerySelectorAsync(selector).ConfigureAwait(false);
            if (element is null)
                throw BrowserElementNotFoundException.For(target, RefEpoch);

            await element.FocusAsync().ConfigureAwait(false);

            if (clear)
            {
                // 三击选中全部再删除：对 input/textarea/contenteditable 都有效
                await _page.Keyboard.DownAsync("Control").ConfigureAwait(false);
                await _page.Keyboard.PressAsync("a").ConfigureAwait(false);
                await _page.Keyboard.UpAsync("Control").ConfigureAwait(false);
                await _page.Keyboard.PressAsync("Backspace").ConfigureAwait(false);
            }

            if (delayMs > 0)
                await _page.Keyboard.TypeAsync(text, new TypeOptions { Delay = delayMs }).ConfigureAwait(false);
            else
                await _page.Keyboard.TypeAsync(text).ConfigureAwait(false);

            if (submit)
                await _page.Keyboard.PressAsync("Enter").ConfigureAwait(false);

            return submit
                ? $"已在 {target} 输入 {text.Length} 个字符并按 Enter"
                : $"已在 {target} 输入 {text.Length} 个字符";
        }, cancellationToken);

    /// <summary>按键（工具用：按一次 = down + up）。</summary>
    /// <remarks>可打印字符走 <c>SendCharacterAsync</c>，理由同 <see cref="KeyDownAsync"/>。</remarks>
    public Task<string> PressKeyAsync(string key, CancellationToken cancellationToken)
        => RunAsync<string>(async () =>
        {
            if (IsPrintableCharacter(key))
            {
                // SendCharacter 自带 down+up
                await _page.Keyboard.SendCharacterAsync(key).ConfigureAwait(false);
                return $"已输入字符 {key}";
            }

            await _page.Keyboard.PressAsync(key).ConfigureAwait(false);
            return $"已按键 {key}";
        }, cancellationToken);

    /// <summary>滚动。</summary>
    public Task<string> ScrollAsync(
        BrowserScrollDirection direction,
        int amount,
        BrowserElementTarget? target,
        CancellationToken cancellationToken)
        => RunAsync<string>(async () =>
        {
            var delta = Math.Clamp(amount <= 0 ? 600 : amount, 1, 20000);
            var (dx, dy) = direction switch
            {
                BrowserScrollDirection.Up => (0, -delta),
                BrowserScrollDirection.Down => (0, delta),
                BrowserScrollDirection.Left => (-delta, 0),
                _ => (delta, 0)
            };

            var selector = target?.ResolveSelector();
            if (selector is not null)
            {
                // 元素内滚动：直接改 scrollTop/scrollLeft（Mouse.Wheel 作用于光标位置，不可靠）
                var script = $$"""
                    (selector) => {
                      const el = document.querySelector(selector);
                      if (!el) return false;
                      el.scrollBy({{dx}}, {{dy}});
                      return true;
                    }
                    """;

                var moved = await _page.EvaluateFunctionAsync<bool>(script, selector).ConfigureAwait(false);
                if (!moved)
                    throw BrowserElementNotFoundException.For(target!.Value, RefEpoch);

                return $"已在 {target!.Value} 内滚动 {delta} 像素";
            }

            await _page.Mouse.WheelAsync(0, dy).ConfigureAwait(false);
            return $"已滚动 {delta} 像素";
        }, cancellationToken);

    /// <summary>等待条件成立。</summary>
    /// <remarks>
    /// <para>
    /// <b>取消必须能打断等待</b>：PuppeteerSharp 的 <c>WaitFor*</c> 系列只接受超时，
    /// 没有 CancellationToken 参数。只用超时的话「用户取消会话」不会产生任何效果 ——
    /// 工具会一直跑到超时才返回，前端看起来永远「执行中」（用户实测报障）。
    /// 因此这里把等待切成小片：片间检查取消令牌，取消后最多一片的延迟就脱离。
    /// </para>
    /// <para>
    /// <b>为什么不用 Task.WaitAsync(ct)</b>：它能达成同样效果，但底层 WaitFor* 任务会继续
    /// 在浏览器里挂着（无法中止），可能影响同页面的后续操作。分片方式能让每次等待都在
    /// 短时间内自然结束。
    /// </para>
    /// <para>
    /// <b>注意两个异常来源</b>：PuppeteerSharp 的等待超时抛的是
    /// <c>WaitTaskTimeoutException</c>（**不是** <see cref="TimeoutException"/> 的子类），
    /// 必须按类型名或基类判定，否则单片的超时会穿透成「工具失败」而不是「继续下一片」。
    /// </para>
    /// </remarks>
    public Task<bool> WaitForAsync(
        BrowserWaitCondition condition,
        TimeSpan timeout,
        CancellationToken cancellationToken)
        => RunAsync<bool>(async () =>
        {
            var deadline = DateTimeOffset.UtcNow + timeout;

            // 分片等待：每片最多 200ms，片间检查取消。
            // 太长的单片会让取消延迟变大；太短会频繁重建 CDP 等待（每次都有开销）。
            const int SliceMs = 200;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var remainingMs = (int)(deadline - DateTimeOffset.UtcNow).TotalMilliseconds;
                if (remainingMs <= 0)
                    throw new TimeoutException($"等待超时（{timeout.TotalSeconds:F0} 秒）");

                var sliceMs = Math.Min(remainingMs, SliceMs);
                var isLastSlice = sliceMs >= remainingMs;

                try
                {
                    if (await TryWaitOnceAsync(condition, sliceMs, cancellationToken).ConfigureAwait(false))
                        return true;

                    // kind=timeout 的语义是「等够时长即成功」，不是「等不到条件则失败」：
                    // 预算用尽时它是成功，其它条件类型才是超时。
                    if (isLastSlice)
                    {
                        if (condition.Kind == BrowserWaitKind.Timeout)
                        {
                            await RefreshIdentityAsync().ConfigureAwait(false);
                            return true;
                        }

                        throw new TimeoutException($"等待超时（{timeout.TotalSeconds:F0} 秒）");
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // 取消优先于超时：明确抛出，让上层落成「已取消」终态。
                    // 注意这一条必须排在超时判定**之前**：Task.Delay(slice, ct) 取消时
                    // 抛的也是 OperationCanceledException，若先按超时吞掉就永远取消不了。
                    throw;
                }
                catch (Exception ex) when (IsWaitTimeout(ex))
                {
                    if (isLastSlice)
                        throw new TimeoutException($"等待超时（{timeout.TotalSeconds:F0} 秒）", ex);

                    // 本片超时但总预算未尽：继续下一片（同时也是取消的响应点）
                }
            }
        }, cancellationToken);

    /// <summary>判定异常是否为「等待超时」（PuppeteerSharp 用自己的类型，不是 BCL 的 TimeoutException）。</summary>
    private static bool IsWaitTimeout(Exception ex)
        => ex is TimeoutException
           || ex is OperationCanceledException
           || ex.GetType().Name.Contains("Timeout", StringComparison.Ordinal);

    /// <summary>单片等待：返回条件是否在本片内成立（超时抛 <see cref="TimeoutException"/>）。</summary>
    private async Task<bool> TryWaitOnceAsync(
        BrowserWaitCondition condition,
        int sliceMs,
        CancellationToken cancellationToken)
    {
        switch (condition.Kind)
        {
            case BrowserWaitKind.Selector:
                await _page.WaitForSelectorAsync(condition.Value!, new WaitForSelectorOptions
                {
                    Timeout = sliceMs
                }).ConfigureAwait(false);
                return true;

            case BrowserWaitKind.Ref:
                // 复用同一构造点（含 ref 形状校验），避免这条路径绕过白名单
                var refSelector = new BrowserElementTarget { Ref = condition.Value }.ResolveSelector();
                await _page.WaitForSelectorAsync(refSelector!, new WaitForSelectorOptions
                {
                    Timeout = sliceMs
                }).ConfigureAwait(false);
                return true;

            case BrowserWaitKind.Text:
                await _page.WaitForFunctionAsync(
                    "(text) => document.body && document.body.innerText.includes(text)",
                    new WaitForFunctionOptions { Timeout = sliceMs },
                    condition.Value!).ConfigureAwait(false);
                return true;

            case BrowserWaitKind.NetworkIdle:
                // 「网络空闲」在真实网站上很难满足：广告、埋点、心跳会一直有零星请求。
                // 单片内最多等 sliceMs（由外层控制在 200ms 量级，保证取消响应快）；
                // 超时视为「本片未空闲」但不算失败 —— 外层会继续下一片直到总预算用尽，
                // 而总预算用尽时按已就绪处理（DOM 通常已可用）。
                // 真正的就绪判定始终交给紧随其后的 browser_snapshot。
                try
                {
                    await _page.WaitForNetworkIdleAsync(new WaitForNetworkIdleOptions
                    {
                        Timeout = sliceMs,
                        IdleTime = Math.Min(500, sliceMs)
                    }).ConfigureAwait(false);
                    return true;
                }
                catch (Exception ex) when (IsWaitTimeout(ex) && !cancellationToken.IsCancellationRequested)
                {
                    return false;   // 本片未空闲，交给外层继续
                }

            default:
                // kind=timeout：固定等待指定时长。
                //
                // 这里**必须**只在整段时间结束时返回 true，不能在每片结束时返回 ——
                // 早期实现每片都 return true，于是第一片（200ms）就「成功」了，
                // 取消与真正的等待时长全部失效（实测：30 秒等待 + 1.5 秒取消 → 报「正常完成」）。
                await Task.Delay(sliceMs, cancellationToken).ConfigureAwait(false);
                return false;   // 让外层按剩余时间继续；真正到期时由外层抛 TimeoutException
        }
    }

    /// <summary>截图。</summary>
    public Task<byte[]> ScreenshotAsync(
        bool fullPage,
        BrowserElementTarget? target,
        CancellationToken cancellationToken)
        => RunAsync<byte[]>(async () =>
        {
            var selector = target?.ResolveSelector();
            if (selector is not null)
            {
                var element = await _page.QuerySelectorAsync(selector).ConfigureAwait(false);
                if (element is null)
                    throw BrowserElementNotFoundException.For(target!.Value, RefEpoch);

                return await element.ScreenshotDataAsync(new ElementScreenshotOptions
                {
                    Type = ScreenshotType.Png
                }).ConfigureAwait(false);
            }

            return await _page.ScreenshotDataAsync(new ScreenshotOptions
            {
                Type = ScreenshotType.Png,
                FullPage = fullPage
            }).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>在页面上下文执行 JS 并返回结果（JSON）。</summary>
    public Task<JsonElement> EvaluateAsync(string expression, bool awaitPromise, CancellationToken cancellationToken)
        => RunAsync<JsonElement>(async () =>
        {
            var script = $$"""
                async (expression, awaitPromise) => {
                  const value = awaitPromise
                    ? await (0, eval)('(' + expression + ')')
                    : (0, eval)('(' + expression + ')');
                  if (value === undefined) return null;
                  try {
                    return JSON.parse(JSON.stringify(value));
                  } catch {
                    return String(value);
                  }
                }
                """;

            return await _page.EvaluateFunctionAsync<JsonElement>(script, expression, awaitPromise)
                .ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>
    /// 更新视口（面板尺寸变化时）。
    /// </summary>
    /// <remarks>
    /// <b>为什么走人工通道</b>：视口大小完全由用户在界面上拖拽面板决定，与 agent 无关。
    /// 走 <see cref="RunAsync"/> 的后果是可见且确定的：页面每次刷新 → WS 重连 →
    /// 前端 <c>onopen</c> 立即 <c>sendResize()</c> → 服务端点亮 <c>busy=1</c> → 遮罩盖上。
    /// 用户看到的是「刷新一下，智能体又说它在操作」，而 agent 根本没在跑。
    /// 更糟的是遮罩会拦截输入，接管按钮自己也被压在下面。
    /// </remarks>
    public Task ResizeAsync(int width, int height, int pixelRatio, CancellationToken cancellationToken)
        => RunHumanAsync<bool>(async () =>
        {
            var clampedWidth = Math.Clamp(width, 320, 4096);
            var clampedHeight = Math.Clamp(height, 240, 4096);

            await _page.SetViewportAsync(new ViewPortOptions
            {
                Width = clampedWidth,
                Height = clampedHeight
            }).ConfigureAwait(false);

            // 记录实际视口与像素比：screencast 重启时用「视口 × 像素比」作
            // maxWidth/maxHeight，既保证布局正确（CSS 像素），又保证出图清晰（物理像素）。
            ViewportWidth = clampedWidth;
            ViewportHeight = clampedHeight;
            PixelRatio = Math.Clamp(pixelRatio, 1, 2);

            await RefreshIdentityAsync().ConfigureAwait(false);
            return true;
        }, cancellationToken);

    // ── 人工输入（工作台画布）─────────────────────────────────────────
    //
    // 这些方法只服务于「人在 canvas 上直接操作」。它们与工具调用共用同一把会话互斥，
    // 因此人工点击不会与 browser_click 交错（否则鼠标按下/抬起状态机会错乱）。
    //
    // <b>但鼠标移动是例外</b>：拖动（滑块验证码、拖拽排序）需要在毫秒级发出成百上千个
    // mousemove，若每一个都抢页级互斥，会与 agent 的工具调用、乃至自身队列相互阻塞，
    // 表现为「拖不动 / 拖动卡顿」。CDP 的 Input.dispatchMouseEvent 本身是顺序执行的指令，
    // 移动指令不与按下/抬起争状态机，因此这里走 <see cref="RunUnlockedAsync"/>：
    // 不进互斥、不改忙碌态。
    //
    // 另一层原因（用户实测报障）：人工移动鼠标若也算「busy」，前端那块
    // 「智能体正在操作」提示会在鼠标移动时每秒闪几十次 —— 而 agent 根本没动作。
    // 忙碌态的正确语义是「agent 的工具正在操作」，不是「有人在动鼠标」。

    /// <summary>移动鼠标到视口坐标（人工拖动路径：不抢互斥、不改忙碌态）。</summary>
    public Task MouseMoveAsync(double x, double y, CancellationToken cancellationToken)
        => RunUnlockedAsync(async () =>
        {
            await _page.Mouse.MoveAsync((decimal)x, (decimal)y).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    /// <summary>在视口坐标按下鼠标（人工：不点亮「智能体正在操作」）。</summary>
    public Task MouseDownAsync(double x, double y, string? button, CancellationToken cancellationToken)
        => RunHumanAsync<bool>(async () =>
        {
            await _page.Mouse.MoveAsync((decimal)x, (decimal)y).ConfigureAwait(false);
            await _page.Mouse.DownAsync(new ClickOptions { Button = ParseButton(button) }).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    /// <summary>在视口坐标抬起鼠标（人工：不点亮「智能体正在操作」）。</summary>
    public Task MouseUpAsync(double x, double y, string? button, CancellationToken cancellationToken)
        => RunHumanAsync<bool>(async () =>
        {
            await _page.Mouse.MoveAsync((decimal)x, (decimal)y).ConfigureAwait(false);
            await _page.Mouse.UpAsync(new ClickOptions { Button = ParseButton(button) }).ConfigureAwait(false);

            // 抬起后刷新地址：人工点击常触发导航，状态条要跟着变
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            await RefreshIdentityAsync().ConfigureAwait(false);

            // 记录在抬起时（而不是按下时）：一次完整点击 = down + up，
            // 只在按下记会让「只按下未抬起」的误触也算一次点击。
            // 右键/中键也记，但标注按钮名——它们通常打开菜单而非导航。
            var label = string.IsNullOrWhiteSpace(button) || button == "left"
                ? $"({x:F0}, {y:F0})"
                : $"({x:F0}, {y:F0}) [{button}]";
            RecordHumanAction("click", label);

            return true;
        }, cancellationToken);

    /// <summary>按原始 delta 滚动（工作台滚轮；人工输入，不点亮忙碌态）。</summary>
    public Task ScrollByDeltaAsync(double deltaX, double deltaY, CancellationToken cancellationToken)
        => RunHumanAsync<bool>(async () =>
        {
            await _page.Mouse.WheelAsync((decimal)deltaX, (decimal)deltaY).ConfigureAwait(false);

            // 滚动记录按主方向概括：逐条记录 deltaY 会让摘要被大量滚动条目淹没
            var direction = Math.Abs(deltaY) >= Math.Abs(deltaX)
                ? (deltaY < 0 ? "向上" : "向下")
                : (deltaX < 0 ? "向左" : "向右");
            RecordHumanAction("scroll", direction);

            return true;
        }, cancellationToken);

    /// <summary>按下某个键（人工输入：不点亮「智能体正在操作」）。</summary>
    /// <remarks>
    /// <b>可打印字符走 <c>SendCharacterAsync</c></b>：PuppeteerSharp 的 <c>DownAsync</c> 按 US 布局表
    /// 查键定义，而 <c>!</c> <c>?</c> <c>:</c> <c>@</c> <c>%</c> 这类 Shift 符号在表里**没有条目**，
    /// 会抛「Unknown key」。异常被 WS 接收循环吞成一条 Debug 日志，用户侧表现为
    /// 「密码/搜索词里少了几个字符」。<c>SendCharacterAsync</c> 直接派发字符，不依赖布局表。
    /// </remarks>
    public Task KeyDownAsync(string key, CancellationToken cancellationToken)
        => RunHumanAsync<bool>(async () =>
        {
            if (IsPrintableCharacter(key))
            {
                // SendCharacter 自带 down+up，因此对应的 KeyUp 无需再发
                await _page.Keyboard.SendCharacterAsync(key).ConfigureAwait(false);

                // 逐字符记录会把摘要彻底淹掉（输入 20 个字就是 20 条）。
                // 只记功能键与少量可打印字符的「一次输入」，正文由 TypeTextAsync 统一记。
                // 这里记的是「用户在画布上直接敲了单个字符」，用于证明发生过人工输入。
                RecordHumanAction("key", key);
                return true;
            }

            await _page.Keyboard.DownAsync(key).ConfigureAwait(false);
            RecordHumanAction("key", key);
            return true;
        }, cancellationToken);

    /// <summary>抬起某个键（人工输入）。</summary>
    /// <remarks>可打印字符已在 <see cref="KeyDownAsync"/> 里用 SendCharacter 完成 down+up，这里是 no-op。</remarks>
    public Task KeyUpAsync(string key, CancellationToken cancellationToken)
        => RunHumanAsync<bool>(async () =>
        {
            if (IsPrintableCharacter(key))
                return true;

            await _page.Keyboard.UpAsync(key).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    /// <summary>
    /// 单个可打印字符（字母、数字、符号、空格）。
    /// </summary>
    /// <remarks>
    /// 不用 <c>ToLowerInvariant()</c>：那会改变字符语义，且对 <c>!</c>/<c>?</c> 毫无帮助
    /// （它们在键定义表里根本不存在）。判定标准是「长度 1 且非控制字符」。
    /// </remarks>
    private static bool IsPrintableCharacter(string key)
        => key.Length == 1 && !char.IsControl(key[0]);

    /// <summary>输入整段文本（输入法提交 / 粘贴；人工输入，不点亮忙碌态）。</summary>
    public Task TypeTextAsync(string text, CancellationToken cancellationToken)
        => RunHumanAsync<bool>(async () =>
        {
            await _page.Keyboard.TypeAsync(text).ConfigureAwait(false);

            // 密码字段必须脱敏：审计日志与告知文本都会读 Value，
            // 明文密码进入日志或模型上下文都是不可接受的（日志常被长期保留与转发）。
            var display = await ShouldMaskTypedValueAsync().ConfigureAwait(false)
                ? "***"
                : text;
            RecordHumanAction("type", "当前焦点元素", display);

            return true;
        }, cancellationToken);

    /// <summary>当前焦点元素是否为敏感输入（password 等），用于决定记录是否脱敏。</summary>
    /// <remarks>
    /// 读 <c>document.activeElement.type</c>：这是唯一能判断「用户刚在密码框里打字」的可靠信号。
    /// 读失败（页面在导航、脚本抛错）时返回 <see langword="true"/> —— 宁可把普通文本也脱敏，
    /// 也不要把密码写进日志。误脱敏只是信息略少，反过来是不可逆的泄露。
    /// </remarks>
    private async Task<bool> ShouldMaskTypedValueAsync()
    {
        try
        {
            var type = await _page.EvaluateFunctionAsync<string>(
                "() => { const el = document.activeElement; return el && el.tagName === 'INPUT' ? (el.type || 'text') : ''; }")
                .ConfigureAwait(false);

            return string.Equals(type, "password", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    private static MouseButton ParseButton(string? button) => button?.ToLowerInvariant() switch
    {
        "right" => MouseButton.Right,
        "middle" => MouseButton.Middle,
        _ => MouseButton.Left
    };

    private TimeSpan ResolveTimeout() => _options.CurrentValue.OperationTimeout;

    /// <summary>发出忙碌状态变化（订阅方异常不得影响页面操作）。</summary>
    private void RaiseBusyChanged()
    {
        var handler = BusyChanged;
        if (handler is null)
            return;

        try
        {
            handler(this);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "通知忙碌状态失败（忽略）");
        }
    }

    /// <summary>
    /// 在会话级互斥内执行一次操作，并刷新活动时间。
    /// </summary>
    private async Task<T> RunAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Exchange(ref _busy, 1);
        RaiseBusyChanged();
        try
        {
            LastActivityAt = DateTimeOffset.UtcNow;
            return await action().ConfigureAwait(false);
        }
        finally
        {
            LastActivityAt = DateTimeOffset.UtcNow;
            Interlocked.Exchange(ref _busy, 0);
            RaiseBusyChanged();
            _gate.Release();
        }
    }

    /// <summary>
    /// 在会话级互斥内执行一次<b>人工输入</b>，但不点亮「智能体正在操作」。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么人工操作不算 busy</b>：那块提示的语义是「agent 的工具正在操作页面」。
    /// 用户自己在画布上点击/拖动时点亮它，等于告诉用户「智能体在操作」——而它并没有。
    /// 用户实测报障：「只要一在里面有操作，智能体正在操作就会一直闪，但智能体实际没有任何操作」。
    /// </para>
    /// <para>
    /// <b>仍然要抢会话互斥</b>：人工点击与 <c>browser_click</c> 若交错，鼠标按下/抬起
    /// 状态机会错乱（两个来源各自 Down/Up 配对，混在一起就分不清谁按下的）。
    /// 只是不进忙碌态。
    /// </para>
    /// </remarks>
    private async Task<T> RunHumanAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LastActivityAt = DateTimeOffset.UtcNow;
            return await action().ConfigureAwait(false);
        }
        finally
        {
            LastActivityAt = DateTimeOffset.UtcNow;
            _gate.Release();
        }
    }

    /// <summary>
    /// 不抢会话互斥地执行一次操作（仅用于鼠标移动这类高频、无状态机的输入）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么不进互斥</b>：拖动滑块（验证码、拖拽排序）要发成百上千个 mousemove。
    /// 每个都抢页级锁会与 agent 的工具调用、乃至自身的队列相互阻塞，
    /// 表现为「拖不动 / 拖动卡顿」。CDP 的鼠标移动指令是顺序派发的，
    /// 不与按下/抬起争状态机，因此可以安全地跳过互斥。
    /// </para>
    /// <para>
    /// <b>为什么也不改忙碌态</b>：同 <see cref="RunHumanAsync"/>——人是操作者，不是 agent。
    /// </para>
    /// </remarks>
    private async Task<T> RunUnlockedAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        // 只刷新活动时间（空闲回收不该把人正在拖动的页面关掉）
        LastActivityAt = DateTimeOffset.UtcNow;
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            LastActivityAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>刷新地址与标题（不抛：页面可能在导航中）。</summary>
    internal async Task RefreshIdentityAsync()
    {
        try
        {
            CurrentUrl = _page.Url;
            CurrentTitle = await _page.GetTitleAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "读取页面地址/标题失败（忽略）");
        }
    }

    private static WaitUntilNavigation[] MapWaitUntil(BrowserWaitUntil waitUntil) => waitUntil switch
    {
        BrowserWaitUntil.Load => [WaitUntilNavigation.Load],
        BrowserWaitUntil.NetworkIdle => [WaitUntilNavigation.Networkidle2],
        _ => [WaitUntilNavigation.DOMContentLoaded]
    };

    private void OnDialog(object? sender, DialogEventArgs e)
    {
        // 弹窗必须处理，否则页面冻结（agent 会看到「点了没反应」）
        var message = $"[{e.Dialog.DialogType}] {e.Dialog.Message}";
        lock (_dialogs)
        {
            if (_dialogs.Count < 10)
                _dialogs.Add(message);
        }

        _ = DismissAsync(e.Dialog);
    }

    private async Task DismissAsync(Dialog dialog)
    {
        try
        {
            await dialog.Dismiss().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "关闭页面弹窗失败（忽略）");
        }
    }

    private void OnConsole(object? sender, ConsoleEventArgs e)
    {
        // 只收错误：正常日志对 agent 是无用噪声
        if (e.Message.Type == ConsoleType.Error)
            _logger.LogDebug("页面控制台错误：{Text}", e.Message.Text);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        _page.Dialog -= OnDialog;
        _page.Console -= OnConsole;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _page.CloseAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "关闭页面时出现异常（忽略）");
        }
        finally
        {
            _gate.Release();

            // 不 Dispose 信号量：此刻可能仍有调用者排队在 WaitAsync 上（人工输入与工具调用竞争），
            // SemaphoreSlim 在被 Dispose 时会让排队者抛 ObjectDisposedException，而那个异常
            // 会从 RunAsync 的 WaitAsync 处逸出（在 try 之外），最终变成一个
            // 「点击元素失败：Cannot access a disposed object.」的误导性失败。
            // 代价只是一个每会话的托管对象，随页面句柄一起被 GC。
        }
    }
}

/// <summary>元素不存在（ref 可能已失效）。</summary>
public sealed class BrowserElementNotFoundException : Exception
{
    /// <summary>创建异常。</summary>
    public BrowserElementNotFoundException(string message) : base(message)
    {
    }

    /// <summary>按定位方式给出可读原因。</summary>
    public static BrowserElementNotFoundException For(BrowserElementTarget target, int refEpoch)
    {
        if (!string.IsNullOrWhiteSpace(target.Ref))
        {
            return new BrowserElementNotFoundException(
                $"页面中没有 ref '{target.Ref}'（当前导航代次 {refEpoch}）；" +
                "请重新调用 `browser_snapshot` 获取最新元素");
        }

        return new BrowserElementNotFoundException(
            $"页面中没有匹配选择器 '{target.Selector}' 的元素；可先用 `browser_snapshot` 查看结构");
    }
}

/// <summary>元素存在但不可交互（不可见 / 被遮挡 / 尺寸为 0）。</summary>
public sealed class BrowserElementNotInteractableException : Exception
{
    /// <summary>创建异常。</summary>
    public BrowserElementNotInteractableException(string message) : base(message)
    {
    }
}
