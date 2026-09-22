namespace Seeing.Agent.Tui.Rendering;

/// <summary>
/// TUI 字形常量（GBK/CP936 安全集）：全部码点均可被系统控制台默认代码页 936 编码往返，
/// 避免字形经编码器 BestFit 回退成字面 <c>?</c>。
/// 已知<b>不可</b>编码（禁用）：U+273B U+276F U+23FA U+280B U+2713 U+2717 U+26D4 U+2298 U+2022。
/// 已实测可编码：<c>√</c> <c>×</c> <c>·</c> <c>…</c> <c>─</c> <c>▌</c> <c>●</c> <c>→</c>。
/// </summary>
internal static class TuiGlyphs
{
    /// <summary>输入提示符（ASCII 最稳）。</summary>
    public const string Prompt = ">";

    /// <summary>推理 / 压缩前缀。</summary>
    public const string Reasoning = "*";

    /// <summary>工具待定 / 运行图标。</summary>
    public const string Tool = "●";

    /// <summary>工具运行中 spinner 帧（ASCII）。</summary>
    public const string Spinner = "|";

    /// <summary>成功。</summary>
    public const string Success = "√";

    /// <summary>失败。</summary>
    public const string Failure = "×";

    /// <summary>拒绝。</summary>
    public const string Rejected = "!";

    /// <summary>取消。</summary>
    public const string Cancelled = "-";

    /// <summary>输入光标。</summary>
    public const string Cursor = "▌";

    /// <summary>暗淡短分隔线字符。</summary>
    public const string Divider = "─";

    /// <summary>列表项目符号（Markdown 无序列表）。</summary>
    public const string Bullet = "·";

    /// <summary>箭头（引用 / 指向）。</summary>
    public const string Arrow = "→";

    /// <summary>省略号（路径/文本截断）。</summary>
    public const string Ellipsis = "…";

    /// <summary>省略号的显示宽度（CJK 环境按全角计）。</summary>
    public const int EllipsisWidth = 2;
}
