using System.Collections.Concurrent;
using Seeing.Agent.Abstractions.Tools;

namespace Seeing.Agent.Core.Modules;

/// <summary>
/// 动态工具贡献注册表（内存实现）— 供结算时按启用模块并入运行时工具 id。
/// </summary>
public sealed class DynamicToolContributorRegistry : IDynamicToolContributorRegistry
{
    private readonly ConcurrentDictionary<string, IDynamicToolContributor> _contributors =
        new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public void Register(IDynamicToolContributor contributor)
    {
        ArgumentNullException.ThrowIfNull(contributor);
        if (string.IsNullOrWhiteSpace(contributor.ModuleId))
            throw new ArgumentException("动态工具贡献者 ModuleId 不能为空", nameof(contributor));

        _contributors[contributor.ModuleId] = contributor;
    }

    /// <inheritdoc />
    public void Unregister(string moduleId)
    {
        if (string.IsNullOrWhiteSpace(moduleId))
            return;

        _contributors.TryRemove(moduleId, out _);
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> GetDynamicToolIds(IEnumerable<string> enabledModuleIds)
    {
        ArgumentNullException.ThrowIfNull(enabledModuleIds);

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var moduleId in enabledModuleIds)
        {
            if (string.IsNullOrWhiteSpace(moduleId))
                continue;
            if (!_contributors.TryGetValue(moduleId, out var contributor))
                continue;

            foreach (var id in contributor.GetDynamicToolIds())
            {
                if (!string.IsNullOrWhiteSpace(id))
                    ids.Add(id.Trim());
            }
        }

        return ids.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
