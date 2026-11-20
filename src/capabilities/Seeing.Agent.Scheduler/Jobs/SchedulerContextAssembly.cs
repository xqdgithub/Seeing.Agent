using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Context;
using Seeing.Agent.Abstractions.Skills;
using Seeing.Session.Core;

namespace Seeing.Agent.Scheduler.Jobs;

/// <summary>
/// Scheduler 旁路的上下文装配：只读模式，不推进会话状态、不插入增量消息、不保存会话。
/// <para>
/// 为什么需要独立作用域：执行作用域事实（会话 cwd / 工作区根 / 钉住技能快照）是 scoped 的，
/// 旁路任务在同一进程内与主链并发，若共用单例就会被互相覆盖。因此每次触发创建一个短作用域，
/// 填充事实后装配，用完即释放。
/// </para>
/// </summary>
internal static class SchedulerContextAssembly
{
    /// <summary>
    /// 旁路装配结果：基线文本（无会话/未注册端口时为 null）与钉住的技能快照。
    /// </summary>
    /// <param name="SystemBaseline">只读装配返回的当前值渲染基线。</param>
    /// <param name="SkillSnapshot">本轮钉住的技能快照（透传到 <c>AgentContext.SkillSnapshot</c>）。</param>
    internal sealed record Result(string? SystemBaseline, SkillCatalogSnapshot? SkillSnapshot);

    /// <summary>
    /// 为旁路执行装配上下文（只读）。
    /// </summary>
    /// <param name="services">任务执行服务容器（根容器）。</param>
    /// <param name="sessionId">目标会话 ID。</param>
    /// <param name="cwd">本轮执行 cwd（执行世界的当前目录）。</param>
    /// <param name="workspaceRoot">工作区根。</param>
    /// <param name="logger">任务日志（缺端口/缺会话必须留下可见告警，禁止静默）。</param>
    /// <param name="ct">取消令牌。</param>
    internal static async Task<Result> TryAssembleAsync(
        IServiceProvider services,
        string sessionId,
        string cwd,
        string workspaceRoot,
        ILogger logger,
        CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var scoped = scope.ServiceProvider;

        var assembler = scoped.GetService<IContextAssembler>();
        var sessionManager = scoped.GetService<ISessionManager>();
        if (assembler is null || sessionManager is null)
        {
            logger.LogWarning(
                "上下文装配端口未注册，旁路执行将无上下文基线: SessionId={SessionId}, Assembler={HasAssembler}, SessionManager={HasSessionManager}",
                sessionId,
                assembler is not null,
                sessionManager is not null);
            return new Result(null, null);
        }

        // 只读加载会话：内存优先，缺失再从存储读；不创建、不替换活实例
        var session = await sessionManager.TryGetOrLoadAsync(sessionId, ct).ConfigureAwait(false);
        if (session is null)
        {
            logger.LogWarning(
                "目标会话不存在，旁路执行将无上下文基线: SessionId={SessionId}",
                sessionId);
            return new Result(null, null);
        }

        var snapshot = await ResolveSnapshotAsync(scoped.GetService<ISkillCatalog>(), logger, ct)
            .ConfigureAwait(false);

        var executionScope = scoped.GetService<IContextExecutionScope>();
        if (executionScope is null)
        {
            // 未注册执行作用域：源将看不到扫描根（按不可用处理），但装配本身仍可给出已存内容
            logger.LogWarning(
                "IContextExecutionScope 未注册，旁路装配的源将无扫描根: SessionId={SessionId}",
                sessionId);
        }
        else
        {
            executionScope.SessionId = sessionId;
            executionScope.Cwd = cwd;
            executionScope.WorkspaceRoot = workspaceRoot;
            executionScope.SkillSnapshot = snapshot;
        }

        try
        {
            // ReadOnly：不写状态、不插入消息（旁路不得改变主链会话事实）
            var result = await assembler
                .EnsureFreshAsync(session, ContextAssemblyMode.ReadOnly, null, null, ct)
                .ConfigureAwait(false);
            return new Result(result.SystemBaseline, snapshot);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 装配失败不阻断定时任务：记录后以「无基线」继续
            logger.LogWarning(
                ex,
                "旁路上下文装配失败，任务将以无上下文基线继续: SessionId={SessionId}",
                sessionId);
            return new Result(null, snapshot);
        }
    }

    /// <summary>
    /// 解析本轮钉住的技能快照：目录刷新失败时告警并退回当前快照，不阻断任务。
    /// </summary>
    private static async Task<SkillCatalogSnapshot?> ResolveSnapshotAsync(
        ISkillCatalog? catalog,
        ILogger logger,
        CancellationToken ct)
    {
        if (catalog is null)
            return null;

        try
        {
            return await catalog.EnsureFreshAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "技能目录刷新失败，旁路将钉住当前快照: Version={Version}",
                catalog.Current.Version);
            return catalog.Current;
        }
    }
}
