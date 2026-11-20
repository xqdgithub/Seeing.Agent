using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Skills;
using Seeing.Session.Core;

namespace Seeing.Agent.Abstractions.Tools;

/// <summary>
/// 一次执行的不可变事实。
/// <para>
/// 唯一构造点只有两处：<c>ToolCallInvoker</c>（主执行链）与 <c>AgentJob</c>（定时/心跳旁路）。
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
/// <param name="Collaborations">会话协同目录（执行级钉住的只读快照）。</param>
/// <param name="Emit">事件出口（可空）。</param>
public sealed record ToolInvocation(
    string SessionId,
    string Cwd,
    AgentDefinition? Agent,
    IPermissionAuthorizer? Authorizer,
    IExecutionToolScope? ToolScope,
    SkillCatalogSnapshot? SkillSnapshot,
    IReadOnlyList<CollaborationDirectory> Collaborations,
    Func<IMessageEvent, ValueTask>? Emit);
