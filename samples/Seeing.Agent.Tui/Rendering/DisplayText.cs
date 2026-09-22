namespace Seeing.Agent.Tui.Rendering;

/// <summary>
/// 终端显示宽度与截断辅助（纯函数）。
/// <para>
/// 终端里一个 CJK/全角字符占 <b>2</b> 个显示格，用 <see cref="string.Length"/> 计宽会低估，
/// 导致状态栏超宽折行、进而破坏活动区高度预算（输入行被挤出屏幕），故按显示格计算。
/// </para>
/// <para>
/// 宽度策略取<b>保守</b>方向：CJK 环境下的「歧义宽度」字符（<c>…</c> <c>·</c> <c>→</c> <c>√</c> 等）
/// 一律按 2 格计。低估会折行，高估只是留下空余，故宁可高估。
/// </para>
/// </summary>
internal static class DisplayText
{
    /// <summary>文本在终端里占用的显示格数。</summary>
    public static int Width(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return 0;

        var width = 0;
        foreach (var ch in value)
            width += IsWide(ch) ? 2 : 1;

        return width;
    }

    /// <summary>
    /// 中间省略截断：保留开头（盘符/根）与结尾（最贴近项目的那一层），中间以 <c>…</c> 替代，
    /// 使结果宽度不超过 <paramref name="maxWidth"/>；太窄时退化为「仅尾部」。
    /// </summary>
    public static string TruncateMiddle(string value, int maxWidth)
    {
        if (string.IsNullOrEmpty(value) || maxWidth <= 0)
            return string.Empty;

        if (Width(value) <= maxWidth)
            return value;

        var head = value.Length > 3 ? value[..3] : value;
        var tailBudget = maxWidth - Width(head) - TuiGlyphs.EllipsisWidth;
        if (tailBudget < MinTailWidth)
        {
            head = string.Empty;
            tailBudget = maxWidth - TuiGlyphs.EllipsisWidth;
        }

        if (tailBudget <= 0)
            return TuiGlyphs.Ellipsis;

        return head + TuiGlyphs.Ellipsis + TakeTail(value, tailBudget);
    }

    /// <summary>把用户主目录前缀缩写为 <c>~</c>（<c>\</c> 与 <c>/</c> 均可识别）。</summary>
    public static string AbbreviateHome(string value, string? home)
    {
        if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(home))
            return value;

        var trimmed = home.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (trimmed.Length == 0
            || value.Length < trimmed.Length
            || !value.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        if (value.Length == trimmed.Length)
            return "~";

        return value[trimmed.Length] is '\\' or '/' ? "~" + value[trimmed.Length..] : value;
    }

    /// <summary>尾部至少保留的显示格数；不够则改为「仅尾部」截断。</summary>
    private const int MinTailWidth = 6;

    /// <summary>从尾部取不超过 <paramref name="budget"/> 显示格的子串；尽量从分隔符处开始（保留完整目录名）。</summary>
    private static string TakeTail(string value, int budget)
    {
        var start = value.Length;
        var width = 0;
        while (start > 0)
        {
            var candidate = IsWide(value[start - 1]) ? 2 : 1;
            if (width + candidate > budget)
                break;

            width += candidate;
            start--;
        }

        var tail = value[start..];

        // 半截片段（如 "g.Agent"）：自首个分隔符起截断，保留完整目录名（含分隔符，读起来是「…\Seeing.Agent」）。
        var separator = tail.IndexOfAny(['\\', '/']);
        if (separator > 0 && separator < tail.Length - 1)
            tail = tail[separator..];

        return tail;
    }

    private static bool IsWide(char ch) => ch switch
    {
        '\u00B7' or '\u00D7' => true,                     // · ×（CJK 环境按全角渲染）
        >= '\u1100' and <= '\u115F' => true,              // 谚文字母
        >= '\u2000' and <= '\u2E7F' => true,              // 通用标点（… → ● ▌ 等）
        >= '\u2E80' and <= '\uA4CF' => true,              // CJK 部首/假名/汉字/彝文
        >= '\uAC00' and <= '\uD7A3' => true,              // 谚文音节
        >= '\uF900' and <= '\uFAFF' => true,              // CJK 兼容表意
        >= '\uFE30' and <= '\uFE6F' => true,              // CJK 兼容形式
        >= '\uFF00' and <= '\uFF60' => true,              // 全角 ASCII
        >= '\uFFE0' and <= '\uFFE6' => true,              // 全角符号
        _ => false,
    };
}
