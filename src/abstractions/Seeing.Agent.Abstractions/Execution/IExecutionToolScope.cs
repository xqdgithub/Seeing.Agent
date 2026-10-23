namespace Seeing.Agent.Abstractions.Execution;

using Seeing.Agent.Abstractions.Tools;

/// <summary>
/// 执行级工具可见性作用域：一次执行内“模型可调用什么”的唯一判定入口。
/// <para>
/// 种子来自本轮结算视图（已下发 schema 的 inline 集 + 延迟暴露的 deferred 集），
/// 生命周期与一次执行相同：执行开始创建、结束丢弃，不落盘、不跨执行继承。
/// </para>
/// <para>
/// 语义：可调用 = 已下发 schema（Inline）∪ 本执行内已加载（tool_search detail=full 命中）。
/// 加载只放宽执行层的放行判定，<b>不改变</b>本轮 <c>tools[]</c>
/// （保住前缀缓存与 <c>SchemaSnapshotEvent</c> 同源）。
/// </para>
/// <para>实现必须线程安全：同一步内的多个工具调用并行执行。</para>
/// </summary>
public interface IExecutionToolScope
{
    /// <summary>该工具在本次执行内是否可直接调用。</summary>
    bool IsCallable(string toolId);

    /// <summary>该工具在本轮结算中是否处于延迟暴露（只能先检索再调用）。</summary>
    bool IsDeferred(string toolId);

    /// <summary>
    /// 登记“本次执行内已加载”。只有本轮结算为延迟态、且尚未加载的工具会生效；
    /// 未知工具、inline 工具与重复登记一律忽略。
    /// </summary>
    /// <returns>实际新增的加载数量。</returns>
    int TryLoad(IEnumerable<string> toolIds);

    /// <summary>本执行内已加载的工具（按加载顺序，去重）。</summary>
    IReadOnlyList<string> LoadedIds { get; }

    /// <summary>
    /// 本轮结算的延迟暴露工具（冻结快照）。检索类工具的语料必须取自这里，
    /// 保证"能搜到什么"与"能调什么"同源，不在执行中途重新结算。
    /// </summary>
    IReadOnlyList<ExposedTool> DeferredTools { get; }
}
