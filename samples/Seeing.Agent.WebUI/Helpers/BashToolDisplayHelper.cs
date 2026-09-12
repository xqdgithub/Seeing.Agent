using System.Text.RegularExpressions;
using Seeing.Agent.WebUI.Models;

namespace Seeing.Agent.WebUI.Helpers;

/// <summary>
/// bash 工具卡展示辅助：清理重复 metadata 文本块、折叠判定、退出码样式。
/// </summary>
public static partial class BashToolDisplayHelper
{
    public const int CollapseLineThreshold = 40;

    [GeneratedRegex(@"\n*<bash_metadata>[\s\S]*?</bash_metadata>\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex BashMetadataBlockRegex();

    /// <summary>
    /// 结构化 Metadata 为 UI 真源；Output 中的 &lt;bash_metadata&gt; 仅为 LLM 可读附录，展示时剥离。
    /// </summary>
    public static string StripBashMetadataBlock(string? output)
    {
        if (string.IsNullOrEmpty(output)) return output ?? string.Empty;
        return BashMetadataBlockRegex().Replace(output, string.Empty).TrimEnd();
    }

    public static bool ShouldCollapseByDefault(string? output, bool isRunning)
    {
        if (isRunning) return false;
        if (string.IsNullOrEmpty(output)) return false;
        var lines = 1;
        foreach (var ch in output)
        {
            if (ch == '\n') lines++;
            if (lines > CollapseLineThreshold) return true;
        }
        return false;
    }

    public static string ExitBadgeText(int? exitCode) =>
        exitCode.HasValue ? $"exit {exitCode.Value}" : "";

    public static string ExitBadgeClass(int? exitCode) =>
        exitCode switch
        {
            null => "bash-exit-unknown",
            0 => "bash-exit-ok",
            _ => "bash-exit-fail"
        };
}
