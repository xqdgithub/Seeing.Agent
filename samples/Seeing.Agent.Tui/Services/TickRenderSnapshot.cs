namespace Seeing.Agent.Tui.Services;

/// <summary>
/// 状态栏外部数据快照（工作目录 / 全局审批开关 / 上下文用量）。
/// <para>
/// 这些值不随「内容变化」产生事件，而是被主循环按帧读取（工作目录可能因切根而变、全局审批可热重载、
/// 用量由 TokenBudget Hook 异步更新）。周期 tick 的脏检查把本快照纳入比对，只有真的变了才重绘。
/// </para>
/// </summary>
internal readonly record struct StatusSignature(TuiBudget? Budget, bool GlobalAutoApprove, string? WorkspaceRoot);

/// <summary>
/// 周期 tick 的重绘判据快照：任一项与「上次已渲染」不同即需要重绘。
/// <para>
/// 用 record struct 的值相等（而非逐项比较链）表达判据：新增一个可见维度只需加字段，
/// 不会出现「加了状态、忘了加比较」的漏判。
/// </para>
/// <para>
/// 覆盖 tick 存在意义的全部来源：流式内容节流后的补绘、执行中状态（工具卡状态 / Esc 提示到期）、
/// 后台执行进度、在途权限/问答数、状态栏外部数据、终端尺寸变化（空闲时改窗口也能及时重排）、
/// 活动区外写入代次（固化提交会拆掉活动区，必须立刻重绘）、内联补全下拉的选择态
/// （候选集 + 游标 + 可见，spec §6.5：hover/方向键改游标须能被 tick 感知并补绘，否则高亮错位）。
/// 其余变化（输入、命令、事件阈值命中）都走强制重绘，不经过本判据。
/// </para>
/// </summary>
internal readonly record struct TickRenderSnapshot(
    long ActiveChars,
    bool Executing,
    int BackgroundExecutions,
    int PendingApprovals,
    StatusSignature Status,
    int Width,
    int Height,
    int TerminalCommits,
    // 选择态脏检查签名（候选拼接 hash + SelectedIndex + 可见）；默认 null 兼容无下拉历史帧与既有测试的位置构造。
    string? CompletionSignature = null);
