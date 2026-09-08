using Seeing.Agent.Abstractions.Modules;

namespace Seeing.Agent.Modules;

/// <summary>
/// 进程级结算输入。Scenario 模块列表通过字典 / 委托注入，避免编译期依赖 BuiltInScenarios。
/// </summary>
public sealed class SettlementInput
{
    /// <summary>宿主登记目录（available）。</summary>
    public required IReadOnlyList<ModuleDescriptor> Available { get; init; }

    /// <summary>seeing.json 的 scenario 名；null 则回退 <see cref="HostDefaultScenario"/>。</summary>
    public string? ConfiguredScenario { get; init; }

    /// <summary>Host Shape 默认 scenario；与 ConfiguredScenario 皆空时 base 为空集。</summary>
    public string? HostDefaultScenario { get; init; }

    /// <summary>
    /// 场景名 → 模块 id 列表。未知场景名告警后 base 为空。
    /// 可与 <see cref="ResolveScenarioModules"/> 并存；委托优先。
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Scenarios { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>可选场景查找缝；返回 null 表示未知场景。</summary>
    public Func<string, IReadOnlyList<string>?>? ResolveScenarioModules { get; init; }

    /// <summary>用户 modules.enabled；null = 使用 scenario base。</summary>
    public IReadOnlyList<string>? UserEnabled { get; init; }

    /// <summary>用户 modules.disabled。</summary>
    public IReadOnlyList<string>? UserDisabled { get; init; }

    /// <summary>
    /// 用户 <c>seams</c> 覆盖（seam 名 → 提供方模块 id）。覆盖 scenario seams。
    /// null = 仅使用 scenario seams。
    /// </summary>
    public IReadOnlyDictionary<string, string>? UserSeams { get; init; }

    /// <summary>
    /// 可选场景 seams 查找缝；返回 null 表示未知场景或无 seams。
    /// 可与 <see cref="Scenarios"/> 并存；委托优先于从场景字典推断。
    /// </summary>
    public Func<string, IReadOnlyDictionary<string, string>?>? ResolveScenarioSeams { get; init; }
}
