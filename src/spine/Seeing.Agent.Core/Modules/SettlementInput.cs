using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Core.CapabilitySets;

namespace Seeing.Agent.Core.Modules;

/// <summary>
/// 进程级结算输入。Boot 决定 Activate 集；Scenario 仅作诊断用默认工作模式名。
/// </summary>
public sealed class SettlementInput
{
    /// <summary>宿主登记目录（available）。</summary>
    public required IReadOnlyList<ModuleDescriptor> Available { get; init; }

    /// <summary>seeing.json 的 <c>Boot</c>；null 则回退 <see cref="HostDefaultBoot"/>，再回退 <c>*</c>。</summary>
    public string? ConfiguredBoot { get; init; }

    /// <summary>宿主/CLI/env 强制 Boot；优先于文件 <see cref="ConfiguredBoot"/>。</summary>
    public string? BootOverride { get; init; }

    /// <summary>Host Shape 默认 Boot；文件未配置时使用。</summary>
    public string? HostDefaultBoot { get; init; }

    /// <summary>
    /// Host Shape 默认 seam 绑定（seam 名 → 提供方模块 id）。
    /// 用户 <see cref="UserSeams"/> 覆盖本表；不再从 Scenario.Seams 读取。
    /// </summary>
    public IReadOnlyDictionary<string, string>? HostDefaultSeams { get; init; }

    /// <summary>
    /// 可选能力集查找缝；未知名返回 null → 结算拒启。
    /// 可与 <see cref="CapabilitySets"/> 并存；委托优先。
    /// </summary>
    public Func<string, CapabilitySetDefinition?>? ResolveCapabilitySet { get; init; }

    /// <summary>
    /// 能力集名 → 定义。未知 Boot 名拒启。
    /// 可与 <see cref="ResolveCapabilitySet"/> 并存；委托优先。
    /// </summary>
    public IReadOnlyDictionary<string, CapabilitySetDefinition> CapabilitySets { get; init; } =
        new Dictionary<string, CapabilitySetDefinition>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// seeing.json 的 scenario 名（进程默认工作模式）；仅写入 <see cref="SettlementResult.Scenario"/> 诊断，
    /// <b>不</b>参与 bootEnabled / Activate。
    /// </summary>
    public string? ConfiguredScenario { get; init; }

    /// <summary>Host Shape 默认 scenario；与 ConfiguredScenario 皆空时 Result.Scenario 为 null。</summary>
    public string? HostDefaultScenario { get; init; }

    /// <summary>
    /// 场景名 → 模块 id 列表。<b>已不再</b>作为 boot base；保留供旧测试/迁移兼容，结算引擎忽略。
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Scenarios { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>可选场景查找缝；结算引擎不再用于 boot base。</summary>
    public Func<string, IReadOnlyList<string>?>? ResolveScenarioModules { get; init; }

    /// <summary>
    /// 用户 modules.enabled。<b>已废除</b>主路径：若传入则警告并忽略，请改用 CapabilitySet + Boot。
    /// </summary>
    public IReadOnlyList<string>? UserEnabled { get; init; }

    /// <summary>用户 modules.disabled（全局模块层黑名单，所有 Boot 都扣）。</summary>
    public IReadOnlyList<string>? UserDisabled { get; init; }

    /// <summary>
    /// 用户 <c>Seams</c> 覆盖（seam 名 → 提供方模块 id）。覆盖 <see cref="HostDefaultSeams"/>。
    /// null = 仅使用 HostDefaultSeams。
    /// </summary>
    public IReadOnlyDictionary<string, string>? UserSeams { get; init; }

    /// <summary>
    /// 可选场景 seams 查找缝。<b>已不再</b>驱动进程 BoundSeams；保留兼容。
    /// </summary>
    public Func<string, IReadOnlyDictionary<string, string>?>? ResolveScenarioSeams { get; init; }
}
