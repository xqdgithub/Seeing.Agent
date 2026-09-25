using System.Text.RegularExpressions;

namespace Seeing.Agent.Tools.Shell;

/// <summary>
/// 剥离终端 ANSI / VT 转义，避免 WebUI 等非 TTY 界面把颜色码当正文显示。
/// </summary>
internal static partial class AnsiEscape
{
    /// <summary>
    /// CSI（如颜色）、OSC、以及常见单字符 ESC 序列。
    /// </summary>
    [GeneratedRegex(
        @"\u001B(?:\[[0-9;?]*[ -/]*[@-~]|][^\u0007\u001B]*(?:\u0007|\u001B\\)|[()][0-9A-Za-z]|[@-Z\\-_])",
        RegexOptions.CultureInvariant)]
    private static partial Regex AnsiRegex();

    public static string Strip(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        return AnsiRegex().Replace(text, string.Empty);
    }
}
