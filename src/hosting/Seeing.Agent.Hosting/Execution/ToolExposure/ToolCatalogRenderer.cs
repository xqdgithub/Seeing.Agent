using System.Text;
using Seeing.Agent.Abstractions.Tools;

namespace Seeing.Agent.Hosting.Execution.ToolExposure;

/// <summary>
/// 目录正文渲染器：把预算裁剪后的 deferred 目录条目渲染为系统提示正文（不含 Heading，由贡献者添加）。
/// 确定性：按条目既有 (namespace, id) 顺序分组输出。
/// </summary>
public static class ToolCatalogRenderer
{
    /// <summary>
    /// 渲染目录正文。无条目时返回 null（贡献者据此不输出该分节）。
    /// </summary>
    public static string? Render(IReadOnlyList<ToolCatalogEntry> entries)
    {
        if (entries is null || entries.Count == 0)
            return null;

        var sb = new StringBuilder();
        sb.Append(
            "以下工具当前未内联进本轮 schema，不可直接调用。" +
            "先用 tool_search 检索（可按 namespace、关键词与 detail 收窄），" +
            "检索命中的工具将自下一轮起变为可直接调用。");
        sb.AppendLine();
        sb.AppendLine();

        var groups = entries
            .GroupBy(e => e.Namespace ?? string.Empty, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        foreach (var group in groups)
        {
            var title = string.IsNullOrEmpty(group.Key) ? "通用" : group.Key;
            sb.AppendLine($"### {title}");
            foreach (var entry in group)
                sb.AppendLine($"- {entry.Signature}");
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }
}
