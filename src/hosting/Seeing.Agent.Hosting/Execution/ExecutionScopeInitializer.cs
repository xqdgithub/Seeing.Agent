using Seeing.Agent.Abstractions.Context;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Skills;
using Seeing.Session.Core;

namespace Seeing.Agent.Hosting.Execution;

/// <summary>
/// 执行级上下文事实的唯一填充点。
/// <para>
/// 主链（<c>ExecutionRunner</c>）与旁路（<c>SchedulerContextAssembly</c>）必须走这里，
/// 避免"某条链路漏填字段"导致上下文源静默落到 Unavailable。
/// </para>
/// </summary>
internal static class ExecutionScopeInitializer
{
    internal static void Initialize(
        IContextExecutionScope scope,
        string sessionId,
        string cwd,
        string workspaceRoot,
        SkillCatalogSnapshot? skillSnapshot,
        string? userQuery,
        SessionToolSchemaResolution? toolSettlement,
        IReadOnlyList<CollaborationDirectory> collaborations)
    {
        scope.SessionId = sessionId;
        scope.Cwd = cwd;
        scope.WorkspaceRoot = workspaceRoot;
        scope.SkillSnapshot = skillSnapshot;
        scope.UserQuery = userQuery;
        scope.ToolSettlement = toolSettlement;
        scope.CollaborationDirectories = collaborations;
    }
}
