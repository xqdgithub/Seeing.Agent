using Seeing.Agent.Abstractions.Files;
using Seeing.Agent.Abstractions.Ui.Workbench;
using Seeing.Agent.WebUI.Files.Services;
using Seeing.Agent.WebUI.Workbench;

namespace Seeing.Agent.WebUI.Files.Workbenches;

/// <summary>
/// 文件工作台的上下文上报内容（纯函数，便于单测）。
/// <para>
/// 删除独立 MD 工作台后，「agent 知道用户在看哪个文档」这件事必须由文件工作台承担
/// （原 <c>MarkdownWorkbench</c> 调 <c>Context.Report</c>，而 <c>FilesWorkbench</c> 从不调）。
/// 上报内容包含可恢复的 <c>path</c> / <c>preview</c> 与给模型看的单行摘要。
/// </para>
/// </summary>
public static class FilesWorkbenchReports
{
    /// <summary>恢复上次文件时用的路径键。</summary>
    public const string PathKey = "path";

    /// <summary>构建激活标签对应的上报载荷。</summary>
    /// <param name="active">当前激活标签。</param>
    /// <returns>上报对象；视图状态含 <c>path</c> 与 <c>preview</c>（"1"/"0"）。</returns>
    public static WorkbenchViewReport For(OpenFile active)
    {
        ArgumentNullException.ThrowIfNull(active);

        var path = active.Id.RelativePath;
        var preview = active.PreviewMode;

        return new WorkbenchViewReport
        {
            ViewState = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [PathKey] = path,
                [LegacyWorkbenchAlias.PreviewKeyName] = preview ? "1" : "0"
            },
            // 摘要进模型上下文，措辞必须能自解释「用户在做什么」
            ContextSummary = preview ? $"正在预览 {path}" : $"正在编辑 {path}"
        };
    }
}
