namespace Seeing.Agent.Abstractions.Ui;

/// <summary>
/// 可见贡献查询端口：给<b>能力层</b>一个「现在真正可用的条目有哪些」的只读视图。
/// <para>
/// <b>为什么不直接读 <see cref="IUiContributionRegistry"/></b>：注册表只给事实（含未启用模块的声明，
/// 那是直链给「未启用」文案所需）。可见性判定只有一份实现（Core 的 <c>UiVisibility</c> + <c>UiScenarioGate</c>），
/// 而能力包受依赖方向约束不能引用 Core。本端口把那份判定包成一个契约，
/// 避免能力层为了「列目录」自己再写一遍模块启用判断（那会是第二份实现，正是要消除的反模式）。
/// </para>
/// <para>
/// 无界面宿主：实现返回空集（没有 UI 就没有「可见」概念），调用方据此给出自解释失败文案。
/// </para>
/// </summary>
public interface IUiContributionQuery
{
    /// <summary>
    /// 取某扩展点上当前可见的贡献（模块启用 + 会话工作模式收窄，与宿主渲染判定完全一致）。
    /// </summary>
    /// <typeparam name="T">载荷类型。</typeparam>
    /// <param name="pointId">扩展点 id。</param>
    /// <param name="sessionScenario">
    /// 会话工作模式名；null = 跟随进程级。仅对 <see cref="UiVisibilityScope.Session"/> 的扩展点生效。
    /// </param>
    IReadOnlyList<T> GetVisible<T>(string pointId, string? sessionScenario = null) where T : UiContribution;
}
