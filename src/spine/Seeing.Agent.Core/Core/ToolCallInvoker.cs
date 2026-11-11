using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Helpers;
using Seeing.Agent.Core.Permission;
using Seeing.Agent.Core.Tools;

namespace Seeing.Agent.Core;

/// <summary>
/// 单个工具调用的执行体：可见集校验 → 权限判定 → 执行 → 终态事件。
/// <para>
/// <b>职责边界：</b>把「一次工具调用」的全过程收在一个类里，产出唯一一个
/// <see cref="ToolCallEvent"/>（Success / Failed / Rejected / Cancelled）。
/// 并发编排、终态排空、事件转发仍留在 <see cref="AgentExecutor"/>——那些是「一轮内多个调用」的语义，
/// 与本类「一个调用」的语义正交。
/// </para>
/// <para>
/// <b>四条终态出口的顺序不可调换</b>：
/// <list type="number">
/// <item>可见集校验失败 → Rejected（不执行、不审批）</item>
/// <item>权限拒绝 → Rejected</item>
/// <item>执行抛异常 → Failed（参数类失败附定义回灌）</item>
/// <item>执行返回 Failure → 若本 Loop 已请求取消则 Cancelled，否则 Failed</item>
/// </list>
/// 取消优先于失败：工具内部吞掉 OCE 返回 Failure 时，只要 Loop 已取消，UI 就不该显示 Failed。
/// </para>
/// </summary>
internal sealed class ToolCallInvoker
{
    private readonly ToolManager _tools;
    private readonly IPermissionService _permissions;
    private readonly ILogger _logger;
    private readonly Seeing.Session.Core.ISessionManager? _sessionManager;

    /// <param name="tools">工具管理器（执行 + 启用状态查询）。</param>
    /// <param name="permissions">权限服务（能力门）。</param>
    /// <param name="logger">日志。</param>
    /// <param name="sessionManager">会话管理器（SubAgent 权限快照合并）。</param>
    public ToolCallInvoker(
        ToolManager tools,
        IPermissionService permissions,
        ILogger logger,
        Seeing.Session.Core.ISessionManager? sessionManager = null)
    {
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sessionManager = sessionManager;
    }

    /// <summary>
    /// 执行单个工具调用（权限 + 执行），返回 Complete 事件。
    /// </summary>
    /// <param name="tc">模型给出的工具调用（名称可能大小写不一致，内部会归一化）。</param>
    /// <param name="agent">当前 Agent（用于权限策略与 Agent 规则）。</param>
    /// <param name="context">执行上下文（携带本轮结算快照与取消令牌）。</param>
    /// <param name="permissionAuthorizer">执行级授权器；缺失时能力门按拒绝处理。</param>
    /// <param name="loopId">当前 Loop id（写入事件供 UI 分组）。</param>
    /// <param name="toolScope">执行级工具作用域（可空）。</param>
    /// <param name="emitAsync">工具进度/流式事件出口（可空）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<ToolCallEvent> InvokeAsync(
        ToolCall tc,
        AgentDefinition agent,
        AgentContext context,
        IPermissionAuthorizer? permissionAuthorizer,
        string loopId,
        IExecutionToolScope? toolScope,
        Func<IMessageEvent, ValueTask>? emitAsync,
        CancellationToken cancellationToken)
    {
        var name = tc.Function?.Name ?? "";
        var arguments = ParseArguments(tc.Function?.Arguments);
        var startTime = DateTime.Now;

        // 调用方（AgentExecutor）已完成可见集校验与工具名归一化：
        // 可用性判定必须留在执行器，本类只负责权限与执行。

        var decision = await EvaluatePermissionAsync(name, arguments, agent, context, permissionAuthorizer, tc.Id)
            .ConfigureAwait(false);

        // 是否需要在本轮失败时回灌定义：本轮未下发 schema 且本执行内未加载过。
        // 两个条件都要：detail=full 加载后工具仍是 Deferred，只看 SendsSchema 会重复附定义。
        var needsDefinitionOnFailure =
            context.ToolSettlement is { } definitionSettlement
            && !definitionSettlement.SendsSchema(name)
            && !(toolScope?.IsCallable(name) ?? false);

        if (decision.Action == PermissionAction.Deny)
        {
            return BuildTerminalEvent(
                tc, name, arguments, context, loopId, ToolCallStatus.Rejected,
                decision.Reason ?? "权限拒绝", metadata: null, startTime);
        }

        ToolResult result;
        // 工具执行的全局兜底超时已下沉到 ToolTimeoutDecorator（工具执行漏斗内，能力感知 + 全局配置热重载）。
        // 此处仅处理取消：Loop 取消令牌贯通工具执行，工具抛 OCE 时统一报告"已取消"。
        try
        {
            result = await _tools.ExecuteAsync(
                tc, context.SessionId, cancellationToken, emitAsync, permissionAuthorizer, agent, toolScope,
                context.SkillSnapshot)
                .ConfigureAwait(false);

            // 工具声明的"本执行内已加载"由执行器统一应用（能力键门控 + 审计）。
            if (result.Success)
                ApplyDeclaredToolLoads(name, result, toolScope);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return BuildTerminalEvent(
                tc, name, arguments, context, loopId, ToolCallStatus.Cancelled, "已取消",
                metadata: null, startTime);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ToolCallInvoker] 工具执行异常: {ToolName}", name);

            // 抛异常的工具（如 GitCommitTool 缺参抛 JsonException）是最典型的参数类失败，
            // 与 result.Success == false 那条出口一样要回灌定义；取消/IO/网络异常除外。
            var exceptionError = ex.Message;
            var exceptionDefinition = needsDefinitionOnFailure && ToolVisibilityGate.IsParamLikeException(ex)
                ? ToolVisibilityGate.ResolveFailureDefinition(name, context)
                : null;
            if (needsDefinitionOnFailure && ToolVisibilityGate.IsParamLikeException(ex) && exceptionDefinition is null)
                exceptionError = ToolVisibilityGate.AppendDefinitionFallbackHint(exceptionError, name, context);

            return new ToolCallEvent
            {
                SessionId = context.SessionId,
                LoopId = loopId,
                Type = MessageEventType.ToolCallComplete,
                ToolCallId = tc.Id,
                ToolName = name,
                Arguments = arguments,
                Status = ToolCallStatus.Failed,
                Error = exceptionError,
                Duration = DateTime.Now - startTime,
                ToolDefinition = exceptionDefinition
            };
        }

        if (!result.Success)
        {
            // 取消优先于失败：工具内部吞掉 OCE 返回 Failure（如 TaskTool 子任务取消）时，
            // 只要本 Loop 已请求取消，终态就应标记为 Cancelled，避免 UI 显示为 Failed。
            if (cancellationToken.IsCancellationRequested)
            {
                return new ToolCallEvent
                {
                    SessionId = context.SessionId,
                    LoopId = loopId,
                    Type = MessageEventType.ToolCallComplete,
                    ToolCallId = tc.Id,
                    ToolName = name,
                    Arguments = arguments,
                    Status = ToolCallStatus.Cancelled,
                    Output = result.Output,
                    Title = result.Title,
                    Error = result.Error ?? "已取消",
                    Duration = DateTime.Now - startTime,
                    Metadata = CopyMetadata(result.Metadata),
                    Parts = result.Parts
                };
            }

            var paramLike = needsDefinitionOnFailure && ToolVisibilityGate.IsParamLikeFailure(result);
            var definition = paramLike ? ToolVisibilityGate.ResolveFailureDefinition(name, context) : null;
            var failureError = result.Error;
            if (paramLike && definition is null)
                failureError = ToolVisibilityGate.AppendDefinitionFallbackHint(failureError, name, context);

            return new ToolCallEvent
            {
                SessionId = context.SessionId,
                LoopId = loopId,
                Type = MessageEventType.ToolCallComplete,
                ToolCallId = tc.Id,
                ToolName = name,
                Arguments = arguments,
                Status = ToolCallStatus.Failed,
                Output = result.Output,
                Title = result.Title,
                Error = failureError,
                Duration = DateTime.Now - startTime,
                Metadata = CopyMetadata(result.Metadata),
                TurnDirective = result.TurnDirective,
                TurnDirectiveReason = result.TurnDirectiveReason,
                ToolDefinition = definition,
                Parts = result.Parts
            };
        }

        return new ToolCallEvent
        {
            SessionId = context.SessionId,
            LoopId = loopId,
            Type = MessageEventType.ToolCallComplete,
            ToolCallId = tc.Id,
            ToolName = name,
            Arguments = arguments,
            Status = ToolCallStatus.Success,
            Output = result.Output,
            Title = result.Title,
            Error = result.Error,
            Duration = DateTime.Now - startTime,
            Metadata = CopyMetadata(result.Metadata),
            TurnDirective = result.TurnDirective,
            TurnDirectiveReason = result.TurnDirectiveReason,
            Parts = result.Parts
        };
    }

    /// <summary>
    /// 应用工具声明的「本执行内已加载」。只有声明了 <see cref="ToolCapabilityKeys.ExposureLoad"/>
    /// 能力的工具才会被采纳；成功应用后写入 <c>exposure.loaded</c> 元数据供审计。
    /// </summary>
    private void ApplyDeclaredToolLoads(string toolName, ToolResult result, IExecutionToolScope? toolScope)
    {
        if (toolScope is null || result.LoadedToolIds is not { Count: > 0 })
            return;

        // 统一能力读取：只认 exposure.load="true"（fail-closed），并解包装饰器到最内层。
        if (!ToolCapabilityReader.GetBool(_tools.GetTool(toolName), ToolCapabilityKeys.ExposureLoad))
        {
            _logger.LogWarning(
                "[ToolCallInvoker] 工具声明加载但未启用 {Key} 能力，已忽略: {ToolName}",
                ToolCapabilityKeys.ExposureLoad, toolName);
            return;
        }

        var added = toolScope.TryLoad(result.LoadedToolIds);
        if (added <= 0)
            return;

        // 第三方工具可能把 Metadata 置 null；审计写入不得把成功调用翻成失败。
        result.Metadata ??= new Dictionary<string, object>();
        result.Metadata["exposure.loaded"] = result.LoadedToolIds.ToArray();
        _logger.LogInformation(
            "[ToolCallInvoker] 工具声明加载已生效: {ToolName}, Count={Count}, Loaded={Loaded}",
            toolName, added, result.LoadedToolIds);
    }

    /// <summary>评估权限（能力门 <c>tool.execute</c>）。</summary>
    private async Task<PermissionDecision> EvaluatePermissionAsync(
        string toolName,
        object? arguments,
        AgentDefinition agent,
        AgentContext context,
        IPermissionAuthorizer? permissionAuthorizer,
        string callId)
    {
        var policy = ResolvePolicy(agent, context);
        var permContext = PermissionIntegrity.FromAgentContext(context, policy, agent.Name);

        var result = await _permissions.EvaluateToolAsync(toolName, null, permContext).ConfigureAwait(false);

        if (result.NeedsConfirmation)
        {
            if (permissionAuthorizer is null)
                return PermissionDecision.Deny("无权限授权器，已拒绝");

            var resolution = await permissionAuthorizer.AuthorizeAsync(new PermissionRequest
            {
                SessionId = string.IsNullOrEmpty(context.SessionId)
                    ? permissionAuthorizer.SessionId
                    : context.SessionId,
                CallId = callId,
                AgentName = agent.Name,
                PermissionKind = "tool.execute",
                Resource = toolName,
                Arguments = arguments,
                Metadata = new Dictionary<string, object>
                {
                    ["arguments"] = arguments ?? new object()
                }
            }, context.CancellationToken).ConfigureAwait(false);

            if (resolution.Decision != PermissionEffect.Allow)
                return PermissionDecision.Deny(resolution.Reason ?? "用户拒绝");

            return PermissionDecision.Allow("用户确认允许");
        }

        return result.ToDecision();
    }

    /// <summary>
    /// 解析权限策略：SubAgent Session 合并 PermissionSnapshot。
    /// </summary>
    private AgentPermissionPolicy ResolvePolicy(AgentDefinition agent, AgentContext context)
    {
        var policy = agent.BuildPermissionPolicy();
        var session = _sessionManager?.Get(context.SessionId);
        if (session?.Kind == Seeing.Session.Core.SessionKind.SubAgent &&
            session.PermissionSnapshot.Count > 0)
        {
            return SessionPermissionMapper.ApplySnapshot(policy, session.PermissionSnapshot);
        }

        return policy;
    }

    private static IReadOnlyDictionary<string, object>? CopyMetadata(Dictionary<string, object>? metadata)
        => metadata is { Count: > 0 } ? new Dictionary<string, object>(metadata) : null;

    /// <summary>
    /// 把工具参数的 JSON 文本解析为对象；失败时原样返回字符串（不抛，保留模型的原始输入供排错）。
    /// </summary>
    private static object? ParseArguments(string? arguments)
    {
        if (string.IsNullOrEmpty(arguments))
            return null;

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(arguments);
        }
        catch
        {
            return arguments;
        }
    }

    /// <summary>构造被拒绝/被取消的终态事件（不含执行结果字段）。</summary>
    private static ToolCallEvent BuildTerminalEvent(
        ToolCall tc,
        string name,
        object? arguments,
        AgentContext context,
        string loopId,
        ToolCallStatus status,
        string error,
        IReadOnlyDictionary<string, object>? metadata,
        DateTime startTime) =>
        new()
        {
            SessionId = context.SessionId,
            LoopId = loopId,
            Type = MessageEventType.ToolCallComplete,
            ToolCallId = tc.Id,
            ToolName = name,
            Arguments = arguments,
            Status = status,
            Error = error,
            Duration = DateTime.Now - startTime,
            Metadata = metadata
        };
}
