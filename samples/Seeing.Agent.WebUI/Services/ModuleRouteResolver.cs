using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Ui;

namespace Seeing.Agent.WebUI.Services;

/// <summary>模块路由解析结果。</summary>
public enum ModuleRouteKind
{
    /// <summary>注册表命中且可渲染。</summary>
    Found,

    /// <summary>已知模块页但未启用 / 未登记。</summary>
    NotEnabled,

    /// <summary>交给 AppAssembly 默认 Router（壳页等仍保留 @page）。</summary>
    Fallback,
}

/// <summary>一次路径解析结果。</summary>
public sealed record ModuleRouteResolution(
    ModuleRouteKind Kind,
    Type? ComponentType = null,
    IReadOnlyDictionary<string, object?>? Parameters = null,
    string? FeatureTitle = null,
    string? ModuleId = null);

/// <summary>
/// 将相对路径解析为 registry 组件 / NotEnabled / AppAssembly 回退。
/// </summary>
public static class ModuleRouteResolver
{
    /// <summary>
    /// 解析 <paramref name="relativePath"/>（可含 query；忽略 hash）。
    /// </summary>
    public static ModuleRouteResolution Resolve(
        string relativePath,
        IUiContributionRegistry registry,
        IModuleCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var path = NormalizePath(relativePath);

        if (TryMatchBest(registry.Routes.Values, path, out var nav, out var parameters))
        {
            if (nav.ComponentType is null)
            {
                return new ModuleRouteResolution(
                    ModuleRouteKind.NotEnabled,
                    FeatureTitle: nav.Title,
                    ModuleId: nav.Requires.FirstOrDefault());
            }

            if (!AreRequirementsEnabled(nav.Requires, catalog))
            {
                return new ModuleRouteResolution(
                    ModuleRouteKind.NotEnabled,
                    FeatureTitle: nav.Title,
                    ModuleId: nav.Requires.FirstOrDefault());
            }

            return new ModuleRouteResolution(
                ModuleRouteKind.Found,
                nav.ComponentType,
                parameters,
                nav.Title,
                nav.Requires.FirstOrDefault());
        }

        if (ModulePageRouteBinder.TryMatchKnown(path, out var known, out var knownParams))
        {
            return new ModuleRouteResolution(
                ModuleRouteKind.NotEnabled,
                FeatureTitle: known.Title,
                ModuleId: known.ModuleId,
                Parameters: knownParams);
        }

        return new ModuleRouteResolution(ModuleRouteKind.Fallback);
    }

    /// <summary>规范化相对路径：去 query/hash、补前导 /、根以外去尾 /。</summary>
    public static string NormalizePath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return "/";

        var path = relativePath.Trim();
        var hash = path.IndexOf('#');
        if (hash >= 0)
            path = path[..hash];
        var query = path.IndexOf('?');
        if (query >= 0)
            path = path[..query];

        if (!path.StartsWith('/'))
            path = "/" + path;

        if (path.Length > 1)
            path = path.TrimEnd('/');

        return string.IsNullOrEmpty(path) ? "/" : path;
    }

    internal static bool TryMatchBest(
        IEnumerable<NavContribution> candidates,
        string path,
        out NavContribution match,
        out IReadOnlyDictionary<string, object?> parameters)
    {
        NavContribution? best = null;
        IReadOnlyDictionary<string, object?>? bestParams = null;
        var bestScore = int.MinValue;

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.Route))
                continue;
            if (!TryMatchTemplate(candidate.Route, path, out var parms, out var score))
                continue;
            if (score <= bestScore)
                continue;
            bestScore = score;
            best = candidate;
            bestParams = parms;
        }

        if (best is null)
        {
            match = null!;
            parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            return false;
        }

        match = best;
        parameters = bestParams!;
        return true;
    }

    /// <summary>
    /// Blazor 风格模板匹配；返回 score（字面段越多越好，其次模板长度）。
    /// </summary>
    public static bool TryMatchTemplate(
        string template,
        string path,
        out IReadOnlyDictionary<string, object?> parameters,
        out int score)
    {
        parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        score = 0;

        var normalizedTemplate = NormalizePath(template);
        var pathSegments = SplitSegments(path);
        var templateSegments = SplitSegments(normalizedTemplate);

        var pathIndex = 0;
        var literalCount = 0;
        var bag = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        for (var t = 0; t < templateSegments.Length; t++)
        {
            var segment = templateSegments[t];
            if (IsCatchAll(segment, out var catchName))
            {
                if (pathIndex >= pathSegments.Length)
                {
                    bag[catchName] = "";
                }
                else
                {
                    bag[catchName] = string.Join('/', pathSegments[pathIndex..]);
                    pathIndex = pathSegments.Length;
                }

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

    private static bool AreRequirementsEnabled(IReadOnlyList<string> requires, IModuleCatalog? catalog)
    {
        if (requires.Count == 0 || catalog is null)
            return true;

        foreach (var id in requires)
        {
            if (!catalog.IsEnabled(id))
                return false;
        }

        return true;
    }

    private static string[] SplitSegments(string path) =>
        path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static bool IsParameter(string segment, out string name)
    {
        if (segment.Length >= 3 && segment[0] == '{' && segment[^1] == '}' && segment[1] != '*')
        {
            name = segment[1..^1];
            return true;
        }

        name = "";
        return false;
    }

    private static bool IsCatchAll(string segment, out string name)
    {
        if (segment.Length >= 4 && segment[0] == '{' && segment[^1] == '}' && segment[1] == '*')
        {
            name = segment[2..^1];
            return true;
        }

        name = "";
        return false;
    }
}
