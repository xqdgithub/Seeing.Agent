namespace Seeing.Agent.Tools.Browser;

/// <summary>
/// 把「新标签 / 弹出窗口」折回会话当前这一页。
/// </summary>
/// <remarks>
/// <para>
/// 架构是每会话一个页面（见浏览器工作台设计）。Chromium 对
/// <c>target="_blank"</c>、<c>&lt;base target&gt;</c>、<c>window.open</c>、Ctrl/中键点击
/// 都会另开一个标签并把它设为前台。画面流订的是原来的那一页，于是：
/// </para>
/// <list type="bullet">
/// <item>有头窗口跟着新标签走，工作台仍停在原页（点百度首页，窗口已是搜索结果）。</item>
/// <item>原页若在后台自己跳走，工作台跟着原页，窗口仍显示前台那个旧标签
/// （点必应结果，工作台已是目标站，窗口还停在搜索页）。</item>
/// </list>
/// <para>
/// 无头没有窗口，但症状相同：点击打开的是另一页，工作台这一页看起来「点了没反应」。
/// 所以不能把这件事归因成有头模式。
/// </para>
/// <para>
/// 脚本在文档创建时把新浏览上下文改回当前页；仍漏掉的弹出页由
/// <c>BrowserPageHandle</c> 的 <c>Popup</c> 收口：关掉弹出页，当前页打开同一地址。
/// </para>
/// </remarks>
internal static class BrowserSingleTabPolicy
{
    /// <summary>弹出页停在空白地址上时，最多再等多少时间等它跳到真正的 URL。</summary>
    internal static readonly TimeSpan PopupUrlTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 在每个新文档执行：链接、表单、<c>window.open</c> 都留在当前页。
    /// </summary>
    /// <remarks>
    /// 必须用 <c>EvaluateFunctionOnNewDocument</c>，不能只在当前文档补一次。
    /// 导航会换文档，漏注册的下一页又会把结果开到新标签。
    /// </remarks>
    internal const string Script = """
        (() => {
          const assign = (url) => {
            if (url == null) return;
            const value = String(url);
            if (!value || value === 'about:blank') return;
            try { location.assign(value); } catch {}
          };

          const dummy = () => {
            const popup = {
              closed: false,
              close() { this.closed = true; },
              focus() {},
              blur() {},
              postMessage() {},
              opener: window
            };
            Object.defineProperty(popup, 'location', {
              configurable: true,
              enumerable: true,
              get() { return popup._href || ''; },
              set(value) { popup._href = String(value); assign(value); }
            });
            return popup;
          };

          // 站点若在解析时缓存了原生 window.open，这里替换已经来不及。
          // 那些漏网的弹出页由 Popup 事件折回，而不是靠这份脚本单独保证。
          window.open = function (url) {
            assign(url);
            return dummy();
          };

          const isNewContext = (el) => {
            let value = (el.getAttribute('target') || '').trim().toLowerCase();
            if (!value) {
              const base = document.querySelector('base[target]');
              value = base ? (base.getAttribute('target') || '').trim().toLowerCase() : '';
            }
            return !!value && value !== '_self' && value !== '_parent' && value !== '_top';
          };

          const retarget = (event) => {
            const node = event.target && event.target.nodeType === 1
              ? event.target
              : (event.target && event.target.parentElement);
            const el = node && node.closest ? node.closest('a[href], area[href]') : null;
            if (!el) return;

            const forced = event.metaKey || event.ctrlKey || event.shiftKey || event.button === 1;
            if (!forced && !isNewContext(el)) return;

            // 修饰键 / 中键不看 target，浏览器仍会新开标签。必须取消默认动作再在当前页打开。
            if (forced && (event.type === 'click' || event.type === 'auxclick')) {
              event.preventDefault();
              event.stopPropagation();
              if (el.href) assign(el.href);
              return;
            }

            el.setAttribute('target', '_self');
          };

          // mousedown 就要改 target：有的浏览器在按下时就决定浏览上下文，等到 click 再改已经晚了。
          document.addEventListener('pointerdown', retarget, true);
          document.addEventListener('mousedown', retarget, true);
          document.addEventListener('click', retarget, true);
          document.addEventListener('auxclick', retarget, true);

          document.addEventListener('submit', (event) => {
            const form = event.target;
            if (!form || form.nodeName !== 'FORM') return;
            if (isNewContext(form))
              form.setAttribute('target', '_self');
          }, true);
        })();
        """;

    /// <summary>
    /// 弹出页地址的三种状态：还没离开空白页、可以打开、必须拒绝。
    /// </summary>
    internal readonly record struct PopupUrlClassification(PopupUrlKind Kind, string? Url, string? Reason);

    /// <summary>
    /// 弹出页的地址能否当作当前页的下一次导航。
    /// </summary>
    /// <remarks>
    /// <c>about:blank</c> 与空地址是「还没跳」，调用方应继续等。
    /// 其余口径与地址栏相同（<see cref="BrowserUrlPolicy"/>），避免弹出页绕过准入。
    /// </remarks>
    internal static PopupUrlClassification Classify(string? url, bool allowPrivateHosts)
    {
        if (string.IsNullOrWhiteSpace(url))
            return new PopupUrlClassification(PopupUrlKind.Pending, null, null);

        var trimmed = url.Trim();
        if (trimmed.Equals("about:blank", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("about:blank#", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("about:blank?", StringComparison.OrdinalIgnoreCase))
            return new PopupUrlClassification(PopupUrlKind.Pending, null, null);

        var verdict = BrowserUrlPolicy.Evaluate(trimmed, allowPrivateHosts);
        return verdict.Allowed
            ? new PopupUrlClassification(PopupUrlKind.Allowed, trimmed, null)
            : new PopupUrlClassification(PopupUrlKind.Denied, null, verdict.Reason);
    }
}

/// <summary>弹出页地址分类。</summary>
internal enum PopupUrlKind
{
    /// <summary>仍是空白页，导航还没提交。</summary>
    Pending,

    /// <summary>可以在当前页打开。</summary>
    Allowed,

    /// <summary>地址策略拒绝。</summary>
    Denied
}
