using Seeing.Agent.Core.Scenarios;
using Seeing.Agent.Core.Ui;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 会话壳的「工作模式解析」薄封装：有效场景名 + 场景默认 Agent。
/// <para>
/// <b>只剩两个转发方法</b>：会话插槽的可见性判定已收到 <c>UiVisibility</c> + <c>UiScenarioGate</c>
/// （<c>SlotHost</c> 直接调用），本类不再承担任何过滤职责；模块启用判定同样只在 Core 有一份实现。
/// 保留它是因为 <c>SessionWindow</c> / <c>SessionToolbar</c> 需要按场景名推导默认 Agent，
/// 而 <c>IScenarioCatalog</c> + <c>BuiltInScenarios</c> 的回退顺序值得留在一处。
/// </para>
/// </summary>
public static class SessionShellVisibility
{
    /// <summary>有效场景名：会话级优先，否则进程级。</summary>
    public static string? ResolveEffectiveScenario(string? sessionScenario, string? processScenario) =>
        UiScenarioGate.ResolveEffective(sessionScenario, processScenario);

    /// <summary>取场景预设的 defaultAgent；未知场景返回 null。</summary>
    public static string? ResolveDefaultAgent(
        string? effectiveScenario,
        IScenarioCatalog? catalog = null)
    {
        if (string.IsNullOrWhiteSpace(effectiveScenario))
            return null;

        var name = effectiveScenario.Trim();
        return catalog?.Get(name)?.DefaultAgent
               ?? BuiltInScenarios.TryGet(name)?.DefaultAgent;
    }
}
