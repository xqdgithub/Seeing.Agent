using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Agent.WebUI.Components.Models;

/// <summary>Provider 列表的一个分组（标题 + 组内条目）。</summary>
/// <param name="Key">稳态分组键（用于渲染 <c>MenuItemGroup.Key</c>）。</param>
/// <param name="Title">分组标题。</param>
/// <param name="Items">组内 Provider，已按显示名排序。</param>
public sealed record ProviderGroup(
    string Key,
    string Title,
    IReadOnlyList<KeyValuePair<string, ProviderInfo>> Items);

/// <summary>
/// 模型页 Provider 列表的分组与排序规则（纯函数，便于单测）。
/// <para><b>为什么按来源分组</b>：用户打开页面通常是「先找自己配的，再看插件带的」。
/// 混排会让一个配置型 Provider 夹在一堆扩展中间，看不出任何规律。</para>
/// <para><b>为什么组内按 Name 而不是 id 排序</b>：列表显示的是 <c>Name</c>。
/// 用 id 排序会出现「看起来乱」的顺序——例如 <c>typesafe-api</c> 显示为 <c>TypeSafe</c>，
/// 位置却由 id 决定。Name 相同才回落到 id 以保证稳定。</para>
/// </summary>
public static class ProviderListGrouping
{
    /// <summary>分组顺序：配置型 → 内置 → 扩展。</summary>
    private static readonly (string Key, string Title, ProviderOrigin Origin)[] s_groups =
    [
        ("configured", "我的配置", ProviderOrigin.Configured),
        ("builtin", "内置", ProviderOrigin.Builtin),
        ("extension", "扩展", ProviderOrigin.Extension),
    ];

    /// <summary>
    /// 把 Provider 快照按来源分组；空组不返回（避免只渲染出一个标题）。
    /// </summary>
    public static IReadOnlyList<ProviderGroup> Build(
        IReadOnlyDictionary<string, ProviderInfo> providers,
        IComparer<string>? nameComparer = null)
    {
        ArgumentNullException.ThrowIfNull(providers);

        var comparer = nameComparer ?? StringComparer.CurrentCultureIgnoreCase;

        return s_groups
            .Select(group => new ProviderGroup(
                group.Key,
                group.Title,
                providers
                    .Where(pair => pair.Value.Origin == group.Origin)
                    .OrderBy(pair => pair.Value.Name ?? pair.Key, comparer)
                    .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .ToList()))
            .Where(group => group.Items.Count > 0)
            .ToList();
    }
}
