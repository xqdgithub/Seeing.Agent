using System.Text.Json;

namespace Seeing.Agent.Tools.Browser;

/// <summary>
/// 快照采集：注入 JS 遍历 DOM，给可交互元素打 <c>data-seeing-ref</c> 并返回元素清单。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不用 CDP 无障碍树</b>：AX 节点的 <c>nodeId</c> 在导航与重新渲染后失效，且不能直接用于
/// <c>Page.ClickAsync</c>——每个元素还要一次 <c>DOM.resolveNode</c> 往返。DOM 遍历一次 JS 拿全量，
/// ref 直接可用 <c>querySelector</c> 命中，点击/输入零额外往返。
/// </para>
/// <para>
/// <b>为什么 ref 写在 DOM 属性上</b>：这是「同一次快照内稳定」的唯一低成本实现——不需要在服务端维护
/// 元素句柄池（句柄会在框架导航时失效且容易泄漏）。
/// </para>
/// <para>
/// JS 与解析逻辑放在一起：它们是一份协议的收与发，分开会导致改了一边忘另一边（症状是
/// 「快照少了一半元素」，且不报错）。
/// </para>
/// </remarks>
public static class BrowserSnapshotBuilder
{
    /// <summary>可选元素上限。</summary>
    public const int MaxElementsLimit = 500;

    /// <summary>默认元素上限。</summary>
    public const int DefaultMaxElements = 200;

    /// <summary>可见文本摘要的最大字符数。</summary>
    public const int VisibleTextLimit = 1200;

    /// <summary>
    /// 采集脚本体：返回 <c>{ url, title, total, elements: [{ref, role, name, states}] }</c>。
    /// </summary>
    /// <param name="maxElements">最多采集多少个元素。</param>
    /// <param name="includeText">是否附带可见文本摘要。</param>
    public static string BuildScript(int maxElements, bool includeText) => $$"""
        () => {
          const MAX = {{Math.Clamp(maxElements, 1, MaxElementsLimit)}};
          const INCLUDE_TEXT = {{(includeText ? "true" : "false")}};
          const SELECTOR = 'a,button,input,select,textarea,summary,[role],[onclick],[contenteditable="true"]';

          const roleOf = (el) => {
            const explicit = el.getAttribute('role');
            if (explicit) return explicit.trim().toLowerCase();
            const tag = el.tagName.toLowerCase();
            if (tag === 'a') return 'link';
            if (tag === 'button') return 'button';
            if (tag === 'select') return 'combobox';
            if (tag === 'textarea') return 'textbox';
            if (tag === 'summary') return 'disclosure';
            if (tag === 'input') {
              const type = (el.getAttribute('type') || 'text').toLowerCase();
              if (type === 'checkbox') return 'checkbox';
              if (type === 'radio') return 'radio';
              if (type === 'submit' || type === 'button' || type === 'reset') return 'button';
              if (type === 'search') return 'searchbox';
              if (type === 'range') return 'slider';
              return 'textbox';
            }
            return tag;
          };

          const labelTextOf = (el) => {
            if (el.id) {
              const label = document.querySelector('label[for="' + CSS.escape(el.id) + '"]');
              if (label && label.innerText) return label.innerText;
            }
            const parent = el.closest('label');
            return parent && parent.innerText ? parent.innerText : '';
          };

          const nameOf = (el) => {
            const raw = el.getAttribute('aria-label')
              || labelTextOf(el)
              || el.getAttribute('placeholder')
              || el.getAttribute('title')
              || (el.value !== undefined && typeof el.value === 'string' && el.value)
              || el.innerText
              || '';
            return String(raw).replace(/\s+/g, ' ').trim().slice(0, 80);
          };

          const visible = (el) => {
            if (el.getAttribute('aria-hidden') === 'true') return false;
            const style = window.getComputedStyle(el);
            if (style.display === 'none' || style.visibility === 'hidden') return false;
            const rect = el.getBoundingClientRect();
            return rect.width > 0 || rect.height > 0;
          };

          const statesOf = (el) => {
            const states = [];
            if (el.disabled) states.push('disabled');
            if (el.getAttribute('aria-checked') === 'true' || el.checked === true) states.push('checked');
            if (el.getAttribute('aria-expanded')) states.push('expanded=' + el.getAttribute('aria-expanded'));
            if (el.required) states.push('required');
            if (el.getAttribute('aria-disabled') === 'true') states.push('disabled');
            return states;
          };

          const nodes = Array.from(document.querySelectorAll(SELECTOR));
          const elements = [];
          let total = 0;
          let index = 0;

          for (const el of nodes) {
            if (!visible(el)) continue;
            total++;
            if (elements.length >= MAX) continue;

            const ref = 'e' + (++index);
            el.setAttribute('data-seeing-ref', ref);
            elements.push({ ref, role: roleOf(el), name: nameOf(el), states: statesOf(el) });
          }

          let text = null;
          if (INCLUDE_TEXT) {
            const body = document.body ? document.body.innerText : '';
            text = String(body).replace(/\n{3,}/g, '\n\n').trim().slice(0, {{VisibleTextLimit}});
            if (!text) text = null;
          }

          return { url: location.href, title: document.title || '', total, elements, text };
        }
        """;

    /// <summary>把采集结果解析为快照。</summary>
    /// <param name="payload">采集脚本的返回值。</param>
    /// <param name="refEpoch">当前导航代次。</param>
    public static BrowserSnapshot Parse(JsonElement payload, int refEpoch)
    {
        var url = ReadText(payload, "url") ?? string.Empty;
        var title = ReadText(payload, "title") ?? string.Empty;
        var total = payload.ValueKind == JsonValueKind.Object
                    && payload.TryGetProperty("total", out var totalElement)
                    && totalElement.ValueKind == JsonValueKind.Number
                    && totalElement.TryGetInt32(out var parsedTotal)
            ? parsedTotal
            : 0;

        var elements = new List<BrowserSnapshotElement>();
        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("elements", out var array)
            && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                var reference = ReadText(item, "ref");
                if (string.IsNullOrWhiteSpace(reference))
                    continue;

                var states = new List<string>();
                if (item.TryGetProperty("states", out var statesElement)
                    && statesElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var state in statesElement.EnumerateArray())
                    {
                        if (state.ValueKind == JsonValueKind.String
                            && state.GetString() is { Length: > 0 } text)
                        {
                            states.Add(text);
                        }
                    }
                }

                elements.Add(new BrowserSnapshotElement(
                    reference,
                    ReadText(item, "role") ?? "element",
                    ReadText(item, "name") ?? string.Empty,
                    states));
            }
        }

        var visibleText = ReadText(payload, "text");

        return new BrowserSnapshot(url, title, refEpoch, elements, Math.Max(total, elements.Count), visibleText);
    }

    private static string? ReadText(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
