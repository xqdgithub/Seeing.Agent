using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Skills;

namespace Seeing.Agent.Abstractions.Tools;

/// <summary>
/// 一次执行的不可变事实。
/// <para>
/// 生产代码的唯一构造点是 <c>ToolCallInvoker</c>；调度/心跳旁路经由
/// <see cref="Agents.AgentContext"/> 进入同一条执行链，再由 <c>ToolCallInvoker</c> 构造本记录。
/// 工具层不得自行拼装或补字段；新增执行级事实应加到这个记录里，而不是给
/// <c>IToolManager.ExecuteAsync</c> 追加可选参数、或给 <c>ToolContext</c> 加散字段。
/// </para>
/// </summary>
/// <param name="SessionId">会话 ID。</param>
/// <param name="Cwd">本次执行的 cwd（相对路径基准）。</param>
/// <param name="Agent">当前执行所属 Agent（可空）。</param>
/// <param name="Authorizer">执行级授权器（可空；为空表示无审批通道）。</param>
/// <param name="ToolScope">执行级工具可见性作用域（可空）。</param>
/// <param name="SkillSnapshot">本轮钉住的技能快照（可空）。</param>
/// <param name="Emit">事件出口（可空）。</param>
/// <param name="ExecutionId">本轮执行 ID（可空；旁路直连执行时由执行器生成）。</param>
public sealed record ToolInvocation(
    string SessionId,
    string Cwd,
    AgentDefinition? Agent,
    IPermissionAuthorizer? Authorizer,
    IExecutionToolScope? ToolScope,
    SkillCatalogSnapshot? SkillSnapshot,
    Func<IMessageEvent, ValueTask>? Emit,
    string? ExecutionId = null);
