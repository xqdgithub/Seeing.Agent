using System.Text;
using Seeing.Agent.Abstractions.Tools;

namespace Seeing.Agent.Hosting.Execution.ToolExposure;

/// <summary>
/// 目录正文渲染器：把命名空间预算分组渲染为系统提示正文（不含 Heading，由贡献者添加）。
/// 确定性：分组顺序、组内条目顺序均已在构建器排定。
/// </summary>
public static class ToolCatalogRenderer
{
    /// <summary>
    /// 渲染目录正文。无分组返回 null（贡献者据此不输出该分节）。
    /// </summary>
    /// <param name="groups">命名空间显示分组（恒含每命名空间与总数，可 0 展示）。</param>
    /// <param name="searchAvailable">本轮可用集是否含 <c>tool_search</c>；false 时不指引检索（避免指向未启用工具）。</param>
    public static string? Render(IReadOnlyList<ToolCatalogNamespace> groups, bool searchAvailable)
    {
        if (groups is null || groups.Count == 0)
            return null;

        var sb = new StringBuilder();
        if (searchAvailable)
        {
            sb.Append(
                "以下工具当前未内联进本轮 schema，不可直接调用。" +
                "先用 tool_search 检索（可按 namespace、关键词与 detail 收窄），" +
                "检索命中的工具将自下一轮起变为可直接调用。");
        }
        else
        {
            sb.Append(
                "以下工具当前未内联进本轮 schema，不可直接调用；本轮未启用工具检索入口，" +
                "如需调用请由宿主启用工具检索能力。");
        }
        sb.AppendLine();
        sb.AppendLine();

        foreach (var group in groups)
        {
            var title = string.IsNullOrEmpty(group.Namespace) ? "通用" : group.Namespace;
            sb.AppendLine($"### {title} ({group.TotalCount} tools, {group.Shown.Count} shown)");
            foreach (var entry in group.Shown)
                sb.AppendLine($"- {entry.Signature}");
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }
}
