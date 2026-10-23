using Seeing.Agent.Tools.Support;

namespace Seeing.Agent.Tui.Services;

/// <summary>一次粘贴的规划结果。</summary>
public sealed record TuiPastePlan(
    IReadOnlyList<string> ImageFilePaths,
    IReadOnlyList<string> PromptPaths,
    string? Text,
    ClipboardImage? Screenshot);

/// <summary>把剪贴板快照转换为附件/路径/文本决策。</summary>
public static class TuiPastePlanner
{
    /// <summary>纯函数规划：文件优先，其次终端文本、剪贴板文本、截图。</summary>
    public static TuiPastePlan Plan(
        string? terminalText,
        IReadOnlyList<ClipboardFile> files,
        string? clipboardText,
        ClipboardImage? image,
        string projectRoot)
    {
        if (files is { Count: > 0 })
        {
            var imagePaths = files.Where(f => f.IsImage).Select(f => f.Path).ToList();
            var promptPaths = files
                .Where(f => !f.IsImage)
                .Select(f => PastedFileLayout.ToPromptPath(projectRoot, Path.GetFullPath(f.Path)))
                .ToList();
            return new TuiPastePlan(imagePaths, promptPaths, null, null);
        }

        if (!string.IsNullOrEmpty(terminalText))
            return new TuiPastePlan([], [], terminalText, null);

        if (!string.IsNullOrEmpty(clipboardText))
            return new TuiPastePlan([], [], clipboardText, null);

        return new TuiPastePlan([], [], null, image);
    }
}
