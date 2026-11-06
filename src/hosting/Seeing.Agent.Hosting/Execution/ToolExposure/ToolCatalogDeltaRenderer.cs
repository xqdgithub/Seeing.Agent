using System.Text;
using System.Text.Json;

namespace Seeing.Agent.Hosting.Execution.ToolExposure;

/// <summary>
/// 工具目录增量叙述：把「命名空间 → 工具 id 集」的 canonical 差异渲染成一段叙述文本。
/// <para>
/// 唯一格式化入口（对齐 <c>SkillCatalogFormatter</c> 的纪律）：装配器只把文本放进
/// <c>context-update</c> 信封，不解析内容。
/// </para>
/// <para>
/// 硬要求：同一 canonical 重复渲染逐字节相等（前缀缓存与增量幂等的前提）；
/// 因此排序一律 ordinal、换行显式写 <c>'\n'</c>（不随平台变化）。
/// </para>
/// </summary>
internal static class ToolCatalogDeltaRenderer
{
    /// <summary>移除条目的固定措辞：明确告知「不得再调用」，避免模型继续尝试。</summary>
    public const string RemovedPhrase = "已不在目录中（不得再调用）";

    private const string Heading = "工具目录已变化：";

    /// <summary>空命名空间的显示名（与目录正文的「通用」一致）。</summary>
    private const string GeneralNamespace = "通用";

    /// <summary>
    /// 渲染差异。无变化返回空串（调用方据此不推进已叙述值）。
    /// </summary>
    /// <param name="previousCanonical">上次已叙述的 canonical；null 视为空目录。</param>
    /// <param name="currentCanonical">本次读取的 canonical；null 视为空目录。</param>
    public static string Render(JsonElement? previousCanonical, JsonElement? currentCanonical)
    {
        var previous = Parse(previousCanonical);
        var current = Parse(currentCanonical);

        var lines = new List<string>();

        foreach (var ns in current.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var currentIds = current[ns];
            var previousIds = previous.TryGetValue(ns, out var known)
                ? known
                : new SortedSet<string>(StringComparer.Ordinal);

            var added = currentIds.Where(id => !previousIds.Contains(id)).ToList();
            var removed = previousIds.Where(id => !currentIds.Contains(id)).ToList();

            if (added.Count > 0)
            {
                lines.Add($"### {ns} (+{added.Count})  {string.Join(", ", added)}");
            }

            if (removed.Count > 0)
            {
                lines.Add($"### {ns} (-{removed.Count})  {string.Join(", ", removed)}（{RemovedPhrase}）");
            }
        }

        // 整体消失的命名空间
        foreach (var ns in previous.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (current.ContainsKey(ns))
                continue;

            var removed = previous[ns].ToList();
            lines.Add($"### {ns} (-{removed.Count})  {string.Join(", ", removed)}（{RemovedPhrase}）");
        }

        if (lines.Count == 0)
            return string.Empty;

        var sb = new StringBuilder(Heading);
        foreach (var line in lines)
            sb.Append('\n').Append(line);

        return sb.ToString();
    }

    /// <summary>canonical → 命名空间 → 排序工具 id 集。形状异常按空处理（不让渲染失败毁掉装配）。</summary>
    private static Dictionary<string, SortedSet<string>> Parse(JsonElement? canonical)
    {
        var result = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);

        if (canonical is not { ValueKind: JsonValueKind.Object } root ||
            !root.TryGetProperty("namespaces", out var namespaces) ||
            namespaces.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var entry in namespaces.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                continue;

            var name = entry.TryGetProperty("name", out var nameElement) &&
                       nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString() ?? GeneralNamespace
                : GeneralNamespace;

            var ids = new SortedSet<string>(StringComparer.Ordinal);
            if (entry.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array)
            {
                foreach (var tool in tools.EnumerateArray())
                {
                    if (tool.ValueKind == JsonValueKind.String && tool.GetString() is { Length: > 0 } id)
                        ids.Add(id);
                }
            }

            result[name] = ids;
        }

        return result;
    }
}
