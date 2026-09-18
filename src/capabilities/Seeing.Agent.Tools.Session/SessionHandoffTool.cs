using System.Text.Json;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Models;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Session.Core;

namespace Seeing.Agent.Core.Tools.Session;

/// <summary>
/// 会话交接工具 — 创建交接后继会话、迁移配置并提交首轮执行；失败时回滚。
/// </summary>
[ToolCapability(ToolCapabilityKeys.TimeoutSkip, "true")]
[ToolCapability(ToolCapabilityKeys.CacheEnabled, "false")]
public sealed class SessionHandoffTool : SessionToolBase
{
    private readonly IExecutionSubmitter _submitter;

    /// <summary>创建 SessionHandoffTool 实例。</summary>
    public SessionHandoffTool(
        ILogger<SessionHandoffTool> logger,
        ISessionManager sessions,
        ISessionGroupManager groups,
        IExecutionSubmitter submitter) : base(logger, sessions, groups)
    {
        _submitter = submitter ?? throw new ArgumentNullException(nameof(submitter));
    }

    /// <inheritdoc />
    public override string Id => "session_handoff";

    /// <inheritdoc />
    public override string Description =>
        "将会话交接给新的后继会话：创建 Clean Root 后继、继承工作目录/模型/思考强度，" +
        "以 prompt 作为首条用户消息并提交执行。需权限审批；提交失败将回滚（删除后继并还原活跃会话）。";

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

        var group = await Groups.GetGroupForSessionAsync(source.Id, ct).ConfigureAwait(false);
        var originalActiveId = group?.ResolveActiveId();

        SessionData? successor = null;
        try
        {
            successor = await Groups.CreateHandoffSuccessorAsync(
                source.Id, agent, title, source.Scenario, ct).ConfigureAwait(false);

            await EnsureInheritedAsync(successor, source, ct).ConfigureAwait(false);

            await Sessions.AddMessageAsync(successor.Id, new SessionMessage
            {
                Role = MessageRole.User,
                Content = prompt,
                CreatedAt = DateTime.UtcNow,
            }, ct).ConfigureAwait(false);

            var submit = await _submitter.SubmitAsync(
                successor.Id,
                new ChatInput { Text = prompt },
                new ChatOptions
                {
                    AgentId = successor.SelectedAgent,
                    ModelId = successor.SelectedModel,
                    SkipUserMessagePersist = true,
                },
                ct).ConfigureAwait(false);

            if (!submit.Success || string.IsNullOrEmpty(submit.ExecutionId))
            {
                if (!string.IsNullOrEmpty(submit.ExecutionId))
                    await _submitter.CancelAsync(submit.ExecutionId, ct).ConfigureAwait(false);

                await RollbackAsync(successor.Id, group?.Id, originalActiveId, ct).ConfigureAwait(false);
                return Failure(submit.Error ?? "交接执行提交失败");
            }

            var result = Success(
                $"会话交接完成。目标会话: {successor.Id}",
                new Dictionary<string, object>
                {
                    ["target_session_id"] = successor.Id,
                    ["execution_id"] = submit.ExecutionId,
                });
            result.TurnDirective = ToolTurnDirective.EndTurn;
            result.TurnDirectiveReason = "handoff";
            return result;
        }
        catch (Exception ex)
        {
            if (successor is not null)
                await RollbackAsync(successor.Id, group?.Id, originalActiveId, ct).ConfigureAwait(false);
            return Failure(ex, "会话交接失败");
        }
    }

    private async Task EnsureInheritedAsync(
        SessionData successor, SessionData source, CancellationToken ct)
    {
        var changed = false;

        if (string.IsNullOrEmpty(successor.WorkingDirectory) && !string.IsNullOrEmpty(source.WorkingDirectory))
        {
            successor.WorkingDirectory = source.WorkingDirectory;
            changed = true;
        }
        if (string.IsNullOrEmpty(successor.SelectedModel) && !string.IsNullOrEmpty(source.SelectedModel))
        {
            successor.SelectedModel = source.SelectedModel;
            changed = true;
        }
        if (string.IsNullOrEmpty(successor.SelectedThinkingEffort) && !string.IsNullOrEmpty(source.SelectedThinkingEffort))
        {
            successor.SelectedThinkingEffort = source.SelectedThinkingEffort;
            changed = true;
        }

        if (changed)
            await Sessions.SaveAsync(successor.Id).ConfigureAwait(false);
    }

    private async Task RollbackAsync(
        string successorId, string? groupId, string? originalActiveId, CancellationToken ct)
    {
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
