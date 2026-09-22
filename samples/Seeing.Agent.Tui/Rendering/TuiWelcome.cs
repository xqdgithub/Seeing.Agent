using Seeing.Agent.Tui.Services;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Seeing.Agent.Tui.Rendering;

/// <summary>
/// 起始页（Logo + 常用操作提示）。仅在「尚未产生任何对话内容且未在执行」时插入活动区顶部，
/// 首次提交后自然消失，不写入滚动历史。
/// <para>
/// 全部文案限 ASCII 与 GBK 可编码字符（见 <see cref="TuiGlyphsTests"/> 同类约束）：
/// 系统控制台默认 CP936，不可编码字符会被编码器回退成字面 <c>?</c>。
/// </para>
/// </summary>
internal static class TuiWelcome
{
    /// <summary>宽度下限：低于此值改用单行 Logo，避免 ASCII 大字被折行破坏版式。</summary>
    internal const int WideBannerMinWidth = 42;

    /// <summary>ASCII 大字 Logo（行尾不留空格，避免编辑器裁剪尾随空白导致错位）。</summary>
    internal static readonly string[] WideBanner =
    [
        """ ____  _____ _____ ___   _   _   ____""",
        """/ ___|| ____|| ____||_ _| | \ | | / ___|""",
        """\___ \|  _|  |  _|   | |  |  \| || |  _""",
        """ ___) | |___ | |___  | |  | |\  || |_| |""",
        """|____/|_____||_____||___| |_| \_| \____|""",
    ];

    /// <summary>窄终端回退 Logo（单行）。</summary>
    internal const string NarrowLogo = "S E E I N G";

    /// <summary>常用操作提示（键位全为 ASCII，故按字符串长度补位即等于显示宽度）。</summary>
    internal static readonly (string Key, string Description)[] Tips =
    [
        ("Enter", "发送消息"),
        ("Ctrl+J", "输入换行"),
        ("Tab", "补全斜杠命令（输入 / 显示候选）"),
        ("/model /agent", "切换模型 / Agent"),
        ("@path", "把文件引用为附件"),
        ("Esc Esc", "取消执行（Ctrl+C 立即取消）"),
        ("/exit Ctrl+D", "退出"),
    ];

    /// <summary>窄终端提示集：中文按双列宽计，长描述会在窄终端折行且续行不缩进，故改用短文案。</summary>
    internal static readonly (string Key, string Description)[] NarrowTips =
    [
        ("Enter", "发送"),
        ("Ctrl+J", "换行"),
        ("Tab", "命令补全"),
        ("@path", "附件"),
        ("Esc Esc", "取消执行"),
        ("/exit", "退出"),
    ];

    private const string TipsHeader = "可用操作";

    // 起始页配色：只用「终端默认前景色」与亮色，避免依赖终端背景明暗。
    // 实测（Spectre 256 色）：grey = ANSI 8、grey11 = xterm 234(#1c1c1c)，
    // 在黑底终端上前者偏暗、后者几乎不可见；default 不产生转义，随主题前景色最稳。
    internal const string LogoStyle = "blue";
    internal const string HeaderStyle = "bold";
    internal const string KeyStyle = "blue";
    internal const string DescriptionStyle = "default";

    /// <summary>
    /// 是否处于起始页：无任何对话类块且未在执行。
    /// 仅 System 块（如状态提示）不影响判定；恢复会话时存在 User/Assistant 块，自然不再显示。
    /// </summary>
    internal static bool IsStartPage(TuiViewState state)
    {
        if (state.IsExecuting)
            return false;

        foreach (var block in state.Blocks)
        {
            if (block.Kind is TuiBlockKind.User or TuiBlockKind.Assistant
                or TuiBlockKind.Tool or TuiBlockKind.Error or TuiBlockKind.Compaction)
            {
                return false;
            }
        }

        return true;
    }

    internal static IRenderable Render(int width)
    {
        var rows = new List<IRenderable>();
        var wide = width >= WideBannerMinWidth;

        if (wide)
        {
            foreach (var line in WideBanner)
                rows.Add(new Markup($"[{LogoStyle}]{Markup.Escape(line)}[/]"));
        }
        else
        {
            rows.Add(new Markup($"[bold {LogoStyle}]{Markup.Escape(NarrowLogo)}[/]"));
        }

        rows.Add(new Text(string.Empty));
        rows.Add(new Markup($"[{HeaderStyle}]{TipsHeader}[/]"));

        var tips = wide ? Tips : NarrowTips;
        var keyWidth = tips.Max(tip => tip.Key.Length);
        foreach (var (key, description) in tips)
        {
            rows.Add(new Markup(
                $"[{KeyStyle}]{Markup.Escape(key.PadRight(keyWidth))}[/]  " +
                $"[{DescriptionStyle}]{Markup.Escape(description)}[/]"));
        }

        return new Rows(rows);
    }
}
