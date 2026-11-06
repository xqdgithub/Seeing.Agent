namespace Seeing.Agent.Abstractions.Execution;

using Seeing.Agent.Abstractions.Tools;

/// <summary>
/// 执行级工具可见性的**只读视图**（供工具/能力包使用）。
/// <para>
/// 暴露"是否已获 schema 授权 / 是否延迟 / 可检索语料"，**不暴露写入能力**：
/// 加载只能由执行器应用 <c>ToolResult.LoadedToolIds</c> 完成。
/// </para>
/// <para>
/// 注意：本视图回答的是「schema 是否已下发」，<b>不等于「能否调用」</b>——
/// 后者只看本会话结算（deferred 工具可直接调用，见 <c>docs/architecture/19</c> §4）。
/// </para>
/// </summary>
public interface IExecutionToolScopeView
{
    /// <summary>该工具在本次执行内是否已获得 schema 授权（内联下发或本执行已加载）。</summary>
    bool IsCallable(string toolId);

    /// <summary>该工具在本轮结算中是否处于延迟暴露（未随 `tools[]` 下发 schema）。</summary>
    bool IsDeferred(string toolId);

    /// <summary>
    /// 本轮结算的延迟暴露工具（冻结快照）。检索类工具的语料必须取自这里，
    /// 保证"能搜到什么"与"能调什么"同源，不在执行中途重新结算。
    /// </summary>
    IReadOnlyList<ExposedTool> DeferredTools { get; }
}

/// <summary>
/// 执行级工具可见性作用域（执行器持有的可写版本）。
/// <para>
/// 种子来自本轮结算视图（已下发 schema 的 inline 集 + 延迟暴露的 deferred 集），
/// 生命周期与一次执行相同：执行开始创建、结束丢弃，不落盘、不跨执行继承。
/// </para>
/// <para>
/// 语义：<see cref="IExecutionToolScopeView.IsCallable"/> 表示「本执行内是否已获得 schema 授权」，
/// <b>不再等于「能否执行」</b>——能否执行只看本会话结算（deferred 工具可直接调用）。
/// 本作用域现用于两件事：判断本次失败是否需要回灌完整定义，以及审计。
/// </para>
/// <para>实现必须线程安全：同一步内的多个工具调用并行执行。</para>
/// </summary>
public interface IExecutionToolScope : IExecutionToolScopeView
{
    /// <summary>
    /// 登记「本次执行内已加载」（审计与观测用）。只有本轮结算为延迟态、且尚未加载的工具会生效；
    /// 未知工具、inline 工具与重复登记一律忽略。
    /// </summary>
    /// <returns>实际新增的加载数量。</returns>
    int TryLoad(IEnumerable<string> toolIds);

    /// <summary>本执行内已加载的工具（按加载顺序，去重）。</summary>
    IReadOnlyList<string> LoadedIds { get; }
}
