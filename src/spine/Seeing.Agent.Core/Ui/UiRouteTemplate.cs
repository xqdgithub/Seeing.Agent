using Seeing.Agent.Abstractions.Ui;

namespace Seeing.Agent.Core.Ui;

/// <summary>路由模板匹配（catch-all 语义与既有测试逐条对齐）。</summary>
public static class UiRouteTemplate
{
    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/";

        var p = path.Trim();

        var hash = p.IndexOf('#');
        if (hash >= 0) p = p[..hash];

        var query = p.IndexOf('?');
        if (query >= 0) p = p[..query];

        if (!p.StartsWith('/')) p = "/" + p;
        if (p.Length > 1) p = p.TrimEnd('/');

        return p.Length == 0 ? "/" : p;
    }

    public static bool TryMatch(
        string template, string path,
        out IReadOnlyDictionary<string, object?> parameters, out int score)
    {
        parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        score = 0;

        var normalizedTemplate = NormalizePath(template);
        var pathSegments = SplitSegments(NormalizePath(path));
        var templateSegments = SplitSegments(normalizedTemplate);

        var pathIndex = 0;
        var literalCount = 0;
        var bag = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var segment in templateSegments)
        {
            if (IsCatchAll(segment, out var catchName))
            {
                // 不解码（%2F 原样）；无尾段捕获空串
                bag[catchName] = pathIndex >= pathSegments.Length
                    ? string.Empty
                    : string.Join('/', pathSegments[pathIndex..]);

                parameters = bag;
                score = literalCount * 1000 + normalizedTemplate.Length;
                return true;
            }

            if (pathIndex >= pathSegments.Length)
                return false;

            if (IsParameter(segment, out var paramName))
            {
                bag[paramName] = pathSegments[pathIndex];
                pathIndex++;
                continue;
            }

            if (!string.Equals(segment, pathSegments[pathIndex], StringComparison.OrdinalIgnoreCase))
                return false;

            literalCount++;
            pathIndex++;
        }

        if (pathIndex != pathSegments.Length)
            return false;

        parameters = bag;
        score = literalCount * 1000 + normalizedTemplate.Length;
        return true;
    }

    /// <summary>
    /// 在候选页面中取最优匹配。
    /// <para>
    /// 同分 tiebreak：路由模板序数序 → 归属模块序数序（与集合枚举顺序无关）。
    /// </para>
    /// <para>
    /// <b>必须传可见性谓词</b>：若对全集直接匹配，停用模块的高分页会抢赢启用模块的页
    /// （旧实现里停用模块的路由根本不在表内，不可能参与竞争）。
    /// </para>
    /// </summary>
    /// <param name="path">实际路径。</param>
    /// <param name="entries">候选页面条目。</param>
    /// <param name="isVisible">可见性谓词；null 表示不过滤（仅供诊断/测试，生产调用方必须传）。</param>
    public static UiRouteMatch? Resolve(
        string path,
        IEnumerable<UiContributionEntry> entries,
        Func<UiContributionEntry, bool>? isVisible = null)
    {
        var normalized = NormalizePath(path);

        PageContribution? bestPage = null;
        IReadOnlyDictionary<string, object?>? bestParameters = null;
        var bestRoute = string.Empty;
        var bestOwner = string.Empty;
        var bestScore = int.MinValue;

        foreach (var entry in entries)
        {
            if (entry.Contribution is not PageContribution page || string.IsNullOrWhiteSpace(page.Route))
                continue;

            if (isVisible is not null && !isVisible(entry))
                continue;

            if (!TryMatch(page.Route, normalized, out var parameters, out var score))
                continue;

            var better = score > bestScore;
            if (!better && score == bestScore)
            {
                var routeCmp = string.CompareOrdinal(page.Route, bestRoute);
                better = routeCmp < 0
                    || (routeCmp == 0 && string.CompareOrdinal(entry.OwnerId, bestOwner) < 0);
            }

            if (!better) continue;

            bestPage = page;
            bestParameters = parameters;
            bestRoute = page.Route;
            bestOwner = entry.OwnerId;
            bestScore = score;
        }

        return bestPage is null ? null : new UiRouteMatch(bestPage, bestParameters!);
    }

    private static string[] SplitSegments(string path)
        => path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static bool IsParameter(string segment, out string name)
    {
        if (segment.Length >= 3 && segment[0] == '{' && segment[^1] == '}' && segment[1] != '*')
        {
            name = segment[1..^1];
            return true;
        }

        name = string.Empty;
        return false;
    }

    private static bool IsCatchAll(string segment, out string name)
    {
        if (segment.Length >= 4 && segment[0] == '{' && segment[^1] == '}' && segment[1] == '*')
        {
            name = segment[2..^1];
            return true;
        }

        name = string.Empty;
        return false;
    }
}
