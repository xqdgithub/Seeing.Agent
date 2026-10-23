using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Session.Core;
using System.Text.Json;

namespace Seeing.Agent.Tools.Session;

/// <summary>
/// 会话交接工具 — 创建交接后继会话、迁移配置并提交首轮执行；失败时回滚。
/// </summary>
[ToolCapability(ToolCapabilityKeys.TimeoutSkip, "true")]
[ToolCapability(ToolCapabilityKeys.CacheEnabled, "false")]
public sealed class SessionHandoffTool : SessionToolBase
{
    private readonly ISessionRuntime _runtime;

    /// <summary>创建 SessionHandoffTool 实例。</summary>
    public SessionHandoffTool(
        ILogger<SessionHandoffTool> logger,
        ISessionManager sessions,
        ISessionGroupManager groups,
        ISessionRuntime runtime) : base(logger, sessions, groups)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    /// <inheritdoc />
    public override string Id => "session_handoff";

    /// <inheritdoc />
    public override string Description =>
        "把当前会话交接给新会话继续；上下文过长时用它保持精简。\n\n" +
        "- 行为：创建 Clean Root 后继，继承工作目录、模型与思考强度。\n" +
        "- 入参：prompt 作为后继首条用户消息并立即提交执行。\n" +
        "- 权限：需审批；提交失败会回滚（删除后继并还原活跃会话）。\n" +
        "- 只想裁剪当前会话用 `session_trim`。";

    /// <inheritdoc />
    public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            prompt = new { type = "string", description = "交接后继的首轮用户提示（必填）" },
            title = new { type = "string", description = "后继会话标题（可选，缺省继承源标题）" },
            agent = new { type = "string", description = "后继会话 Agent（可选，缺省继承源 Agent）" }
        },
        required = new[] { "prompt" }
    });

    /// <inheritdoc />
    public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        var ct = context.CancellationToken;
        var prompt = GetStringArgument(arguments, "prompt");
        if (string.IsNullOrWhiteSpace(prompt))
            return Failure("prompt 参数是必需的");

        var title = GetStringArgument(arguments, "title");
        var agent = GetStringArgument(arguments, "agent");

        var denied = await AuthorizeAsync(
            context,
            Id,
            "会话交接需要授权",
            new Dictionary<string, object> { ["prompt_length"] = prompt.Length },
            ct).ConfigureAwait(false);
        if (denied is not null)
            return denied;

        SessionData source;
        try
        {
            source = await Sessions.GetOrLoadAsync(context.SessionId, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return Failure($"会话不存在: {context.SessionId}");
        }

        // 幂等预检：装饰器重试同一 CallId 时，复用已创建的后继，避免重复交接
        var inflightKey = string.IsNullOrEmpty(context.CallId)
            ? null
            : "handoff_inflight:" + context.CallId;
        if (inflightKey is not null
            && source.Metadata.TryGetValue(inflightKey, out var inflightTarget)
            && !string.IsNullOrEmpty(inflightTarget))
        {
            var existing = await Sessions.TryGetOrLoadAsync(inflightTarget).ConfigureAwait(false);
            if (existing is not null)
            {
                var replay = Success(
                    $"会话交接已完成。目标会话: {inflightTarget}",
                    new Dictionary<string, object> { ["target_session_id"] = inflightTarget });
                replay.TurnDirective = ToolTurnDirective.EndTurn;
                replay.TurnDirectiveReason = "handoff";
                return replay;
            }
        }

        var group = await Groups.GetGroupForSessionAsync(source.Id, ct).ConfigureAwait(false);

        // 仅当前锚点（最新主线会话）可被交接；单成员冷兜底（group==null）不误判
        var member = group?.Members.FirstOrDefault(m =>
            string.Equals(m.SessionId, source.Id, StringComparison.Ordinal));
        if (member is { IsAnchor: false })
            return Failure("仅当前主线会话（锚点）可发起交接；请回到最新会话后再试。");

        var originalActiveId = group?.ResolveActiveId();

        string? createdSuccessorId = null;
        try
        {
            var successor = await Groups.CreateHandoffSuccessorAsync(
                source.Id, agent, title, source.Scenario, ct).ConfigureAwait(false);
            createdSuccessorId = successor.Id;

            // 提交前写入在途标记：同一 CallId 重试可直接复用该目标
            if (inflightKey is not null)
            {
                await Sessions.UpdateSessionAsync(
                    source.Id, s => s.Metadata[inflightKey] = successor.Id, ct).ConfigureAwait(false);
            }

            // 后继 user 消息由 DeliverAsync 统一落盘（不再预落盘）。
            var submit = await _runtime.DeliverAsync(new InboxMessage
            {
                TargetSessionId = successor.Id,
                Sender = new InboxSender { Kind = InboxSenderKind.Human },
                Body = prompt,
                Delivery = InboxDelivery.Wake,
                Presentation = InboxPresentation.User,
                AgentId = successor.SelectedAgent,
                ModelId = successor.SelectedModel,
            }, ct).ConfigureAwait(false);

            if (!submit.Success)
            {
                await _runtime.StopAsync(successor.Id, CancellationToken.None).ConfigureAwait(false);
                await RollbackAsync(successor.Id, group?.Id, originalActiveId).ConfigureAwait(false);
                return Failure(submit.Error ?? "交接执行提交失败");
            }

            var result = Success(
                $"会话交接完成。目标会话: {successor.Id}",
                new Dictionary<string, object>
                {
                    ["target_session_id"] = successor.Id,
                });
            result.TurnDirective = ToolTurnDirective.EndTurn;
            result.TurnDirectiveReason = "handoff";
            return result;
        }
        catch (Exception ex)
        {
            if (createdSuccessorId is not null)
                await RollbackAsync(createdSuccessorId, group?.Id, originalActiveId).ConfigureAwait(false);
            return Failure(ex, "会话交接失败");
        }
    }

    /// <summary>
    /// 回滚交接：移除后继并还原活跃会话。
    /// <para>内部一律使用 <see cref="CancellationToken.None"/>：提交失败常因取消令牌已触发，
    /// 若沿用该令牌，组锁 <c>WaitAsync</c> 会立即抛 <see cref="OperationCanceledException"/>，
    /// 导致后继成为孤儿并劫持锚点（P1-15）。回滚是清理路径，必须与调用方取消状态解耦。</para>
    /// </summary>
    private async Task RollbackAsync(
        string successorId, string? groupId, string? originalActiveId)
    {
        var ct = CancellationToken.None;
        try
        {
            await Groups.RemoveSessionAsync(successorId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "会话交接回滚：移除后继会话失败 SuccessorId={SuccessorId}", successorId);
        }

        if (groupId is not null && originalActiveId is not null)
        {
            try
            {
                await Groups.SetActiveAsync(groupId, originalActiveId, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "会话交接回滚：还原活跃会话失败 GroupId={GroupId}, ActiveId={ActiveId}",
                    groupId,
                    originalActiveId);
            }
        }
    }
}
