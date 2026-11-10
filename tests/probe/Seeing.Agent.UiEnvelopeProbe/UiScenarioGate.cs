using Seeing.Agent.Core.Scenarios;

namespace Seeing.Agent.UiEnvelopeProbe;

/// <summary>
/// 会话工作模式 → 模块集解析（唯一实现）。
/// <para>
/// 从 WebUI 的 <see cref="SessionShellVisibility.ResolveScenarioModuleSet"/> 上移：
/// 它依赖 <c>IScenarioCatalog</c> 与 <c>BuiltInScenarios</c>，两者都在 Core。
/// </para>
/// <para>
/// <b>五个分支必须逐条等价</b>（旧实现 <c>SessionShellVisibility.cs:115-127</c>）：
/// ① 空白 → null（不收敛）；② 未知模式 → null；③ 声明通配 <c>"*"</c> → null；
/// ④ 否则返回 OrdinalIgnoreCase 的模块集（<b>空集也返回空集，不返回 null</b>——语义不同，见下）。
/// </para>
/// </summary>
public static class UiScenarioGate
{
    /// <summary>
    /// 解析工作模式的模块集。
    /// </summary>
    /// <param name="effectiveScenario">有效工作模式名（会话级优先，否则进程级）。</param>
    /// <param name="catalog">工作模式目录（可空，缺失时回退内置）。</param>
    /// <returns>
    /// <b>null = 不收敛</b>（未指定 / 未知 / 通配）；
    /// <b>空集 = 全部隐藏</b>（工作模式存在但模块列表为空）。
    /// 两者语义相反，不可合并。
    /// </returns>
    public static IReadOnlySet<string>? Resolve(string? effectiveScenario, IScenarioCatalog? catalog)
    {
        if (string.IsNullOrWhiteSpace(effectiveScenario))
            return null;

        var name = effectiveScenario.Trim();
        var definition = catalog?.Get(name) ?? BuiltInScenarios.TryGet(name);

        if (definition is null)
            return null;                                   // 未知模式：不收敛

        if (BuiltInScenarios.IncludesAllModules(definition.Modules))
            return null;                                   // 通配 "*"：不收敛

        return new HashSet<string>(definition.Modules, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 计算有效工作模式名：会话级优先，否则进程级。
    /// <para>与 <c>SessionShellVisibility.ResolveEffectiveScenario</c>（<c>:14-17</c>）逐字符等价。</para>
    /// </summary>
    public static string? ResolveEffective(string? sessionScenario, string? processScenario)
        => string.IsNullOrWhiteSpace(sessionScenario)
            ? (string.IsNullOrWhiteSpace(processScenario) ? null : processScenario.Trim())
            : sessionScenario.Trim();
}
