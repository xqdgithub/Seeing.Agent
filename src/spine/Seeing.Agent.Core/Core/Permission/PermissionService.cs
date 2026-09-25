using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Models;
using System.Security.Cryptography;

using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Interactions;
using Seeing.Agent.Abstractions.Permissions;
namespace Seeing.Agent.Core.Permission;

/// <summary>
/// 权限服务实现 - 统一的权限评估入口
/// </summary>
public class PermissionService : IPermissionService
{
    private readonly ILogger<PermissionService> _logger;
    private readonly byte[] _hmacKey;
    private readonly IPermissionGrantStore? _grantStore;
    private readonly EffectivePermissionPolicy? _effectivePolicy;
    private readonly IWorkspacePathGate? _workspaceGate;
    private readonly IOptionsMonitor<SeeingAgentOptions>? _options;
    private readonly IPermissionRequestManager? _requestManager;
    private readonly IPermissionSurfaceRegistry? _presentation;
    private readonly IEnumerable<IPermissionChannel> _channels;
    private readonly IAgentRegistry? _agentRegistry;

    /// <summary>
    /// 构造权限服务，注入授权决策链所需的可选依赖（白名单存储、生效策略、通道等，均可缺省）。
    /// </summary>
    public PermissionService(
        ILogger<PermissionService> logger,
        IPermissionGrantStore? grantStore = null,
        EffectivePermissionPolicy? effectivePolicy = null,
        IWorkspacePathGate? workspaceGate = null,
        IOptionsMonitor<SeeingAgentOptions>? options = null,
        IPermissionRequestManager? requestManager = null,
        IPermissionSurfaceRegistry? presentation = null,
        IEnumerable<IPermissionChannel>? channels = null,
        IAgentRegistry? agentRegistry = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _grantStore = grantStore;
        _effectivePolicy = effectivePolicy;
        _workspaceGate = workspaceGate;
        _options = options;
        _requestManager = requestManager;
        _presentation = presentation;
        _channels = channels ?? Array.Empty<IPermissionChannel>();
        _agentRegistry = agentRegistry;

        // 生成或加载 HMAC 密钥
        _hmacKey = LoadOrGenerateHmacKey();
    }

    /// <inheritdoc />
    public async Task<PermissionResult> EvaluateToolAsync(
        string toolName,
        string? ns,
        PermissionContext context,
        CancellationToken cancellationToken = default)
    {
        var resource = new ResourceIdentifier(PermissionKind.Tool, toolName, ns);
        return await EvaluateResourceAsync(resource, context, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PermissionResult> EvaluateSkillAsync(
        string skillName,
        PermissionContext context,
        CancellationToken cancellationToken = default)
    {
        var resource = new ResourceIdentifier(PermissionKind.Skill, skillName);
        return await EvaluateResourceAsync(resource, context, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void InvalidateCache(string? agentName = null, string? resourcePattern = null)
    {
        // 旧的 5 分钟判定结果缓存已移除（修 S2 陈旧判定）。
        // 规则编译快照当前不缓存（无带版本号的策略提供方，缓存会重引入陈旧风险；见 spec §8 YAGNI）。
        _logger.LogDebug(
            "权限缓存失效请求（无判定结果缓存 / 无规则快照缓存）: Agent={AgentName} Resource={ResourcePattern}",
            agentName,
            resourcePattern);
    }

    /// <inheritdoc />
    public Task LogAuditAsync(PermissionResult result, PermissionContext context, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "权限评估: {Effect} {Resource} for Agent={Agent} Session={Session} Reason={Reason}",
            result.Effect,
            result.Resource,
            context.AgentName,
            context.SessionId,
            result.Reason);

        // 可以扩展为写入审计日志文件或发送到审计服务
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<PermissionResolution> AuthorizeAsync(PermissionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var resolution = await AuthorizeCoreAsync(request, ct).ConfigureAwait(false);
        await AuditAsync(request, resolution, ct).ConfigureAwait(false);
        return resolution;
    }

    #region AuthorizeAsync 决策链（spec §5.1）

    private async Task<PermissionResolution> AuthorizeCoreAsync(PermissionRequest request, CancellationToken ct)
    {
        // 0. 规范化
        var sessionId = request.SessionId ?? string.Empty;
        var requestId = string.IsNullOrEmpty(request.RequestId)
            ? Guid.NewGuid().ToString("N")
            : request.RequestId!;
        var normalized = request with { SessionId = sessionId, RequestId = requestId };

        var isFilesystem = IsFilesystemKind(normalized.PermissionKind);
        var restrict = _options?.CurrentValue.Workspace.RestrictToWorkspace == true;

        // 1. 工作区边界预检（与 WorkspacePathGate.EnsureAllowed 同源）
        if (isFilesystem && restrict)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                return Result(normalized, PermissionEffect.Deny, PermissionResolvedBy.Policy,
                    "缺少会话 ID，无法在硬边界模式下访问文件");
            }

            if (_workspaceGate is not null)
            {
                var gateError = _workspaceGate.EnsureAllowed(sessionId, normalized.Resource ?? string.Empty);
                // RequireInteraction=true 时不被工作区内/白名单静默放行，落入后续询问（spec §5.1 契约）。
                if (gateError is null && !normalized.RequireInteraction)
                {
                    return Result(normalized, PermissionEffect.Allow, PermissionResolvedBy.Policy,
                        "路径在工作区内或会话白名单内");
                }
            }
            // 越界（含 filesystem.workspace_extend）→ 继续 2/3/5（可经审批扩权）
        }

        // 2. 授权记忆
        var grant = MatchGrant(normalized);
        if (grant is not null)
        {
            return Result(normalized, grant.Effect, PermissionResolvedBy.Policy,
                $"命中授权记忆（{grant.Scope}）");
        }

        // 3. Agent 规则（kind 经 PermissionKindMapper 映射）
        var policy = await ResolveAgentPolicyAsync(normalized, ct).ConfigureAwait(false);
        if (policy is not null)
        {
            var (matchedRule, ruleEffect, ruleReason) = await EvaluateRulesAsync(
                new ResourceIdentifier(PermissionKindMapper.Map(normalized.PermissionKind), normalized.Resource ?? string.Empty),
                BuildPolicyContext(normalized, policy),
                ct).ConfigureAwait(false);

            // 3a. Deny 对所有 kind 生效（fail-safe；激活 explore/plan 的 Deny(Shell,"*") 等）。
            //     注意：仅"匹配到的 Deny 规则"硬拒；策略默认效果（无匹配规则）Deny 不短路资源类 kind，
            //     否则子代理（如 explore 默认 Deny）的文件/shell 访问会被秒拒而永远走不到审批
            //     （release notes §3.5「资源门仅应用 Deny 规则」；§5 要求资源门进入询问）。
            if (ruleEffect == PermissionEffect.Deny &&
                (matchedRule is not null || !IsResourceKind(normalized.PermissionKind)))
                return Result(normalized, PermissionEffect.Deny, PermissionResolvedBy.Policy, ruleReason);

            // 3b. Allow 仅对非资源类 kind 短路（tool.execute / skill.execute / mcp.* / agent.*）。
            //     资源类 kind（filesystem.* / shell.* / network.*）忽略 Agent 规则的 Allow：
            //     旧系统中 EvaluateFileAsync/EvaluateMcpToolAsync/EvaluateAgentAsync 无生产调用者（死代码），
            //     资源门（ToolManager）从不套用 Agent 规则；工作区内已由步骤 1b 放行，
            //     越界必须走 4/5/6 审批，不得被 build 的 Allow(File/Shell/Network,"*") 绕过。
            if (ruleEffect == PermissionEffect.Allow && !normalized.RequireInteraction
                && !IsResourceKind(normalized.PermissionKind))
                return Result(normalized, PermissionEffect.Allow, PermissionResolvedBy.Policy, ruleReason);
        }

        // 4. 生效开关（实时）
        var toggle = _effectivePolicy?.Resolve(normalized);
        if (toggle == PermissionEffect.Allow)
            return Result(normalized, PermissionEffect.Allow, PermissionResolvedBy.Policy, "生效开关自动批准");

        // 强制交互：会话/覆盖 Disabled（Ask）或 RequireInteraction=true 时短路宿主通道自动批准，直接进入询问
        // （spec §5.1「RequireInteraction=true → 跳过以上 Allow 分支」；§5.3 Resolve 恒 null 只解决步骤 4）。
        var forceInteraction = toggle == PermissionEffect.Ask || normalized.RequireInteraction;

        // 5. 宿主通道策略（任一 TryAutoApprove=Allow 即放行）
        if (!forceInteraction)
        {
            foreach (var channel in _channels)
            {
                PermissionEffect? autoApprove;
                try
                {
                    autoApprove = channel.TryAutoApprove(normalized);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "宿主权限通道自动批准判定失败: {Channel}", channel.GetType().Name);
                    continue;
                }

                if (autoApprove == PermissionEffect.Allow)
                    return Result(normalized, PermissionEffect.Allow, PermissionResolvedBy.Policy, "宿主通道自动批准");
            }
        }

        // 6. 询问
        if (_presentation is null || !_presentation.CanSurface(sessionId))
            return Result(normalized, PermissionEffect.Deny, PermissionResolvedBy.NoChannel, "无交互通道");

        if (_requestManager is null)
            return Result(normalized, PermissionEffect.Deny, PermissionResolvedBy.NoChannel, "权限请求管理器不可用");

        var ticket = await _requestManager.BeginAsync(normalized, ct).ConfigureAwait(false);
        await PresentAsync(normalized, ct).ConfigureAwait(false);
        var resolution = await _requestManager.WaitAsync(ticket, ct).ConfigureAwait(false);

        // 7. 决策后处理
        if (resolution.Decision == PermissionEffect.Allow && isFilesystem && restrict &&
            !string.IsNullOrWhiteSpace(normalized.Resource))
        {
            // 7a. 越界审批放行后写入会话白名单——仅 SessionDirectory 及以上 Scope；
            //     Once 批准不写（单次批准不得放大为整目录免审，spec §1.2 收紧）。
            //     正确的目录级记忆语义由 7b 以 kind 精确 + 目录前缀覆盖。
            if (resolution.Scope != PermissionGrantScope.Once)
            {
                var directory = ResolveResourceDirectory(normalized.Resource!);
                if (!string.IsNullOrEmpty(directory) && _grantStore is not null)
                    _grantStore.AddSessionDirectory(sessionId, directory);
            }
        }

        // 7b. Scope != Once → 写入决策记忆（Allow/Deny 均记）。
        //     SessionDirectory 须以目录前缀写入（Lookup 靠前缀语义命中同目录兄弟文件），其余 Scope 用精确资源。
        if (resolution.Scope != PermissionGrantScope.Once && _grantStore is not null)
        {
            var memoryResource = normalized.Resource;
            if (resolution.Scope == PermissionGrantScope.SessionDirectory &&
                isFilesystem &&
                !string.IsNullOrWhiteSpace(normalized.Resource))
            {
                var directory = ResolveResourceDirectory(normalized.Resource!);
                if (!string.IsNullOrEmpty(directory))
                    memoryResource = directory;
            }

            _grantStore.Add(sessionId, new PermissionGrant(
                normalized.PermissionKind, memoryResource, resolution.Scope, resolution.Decision));
        }

        return resolution;
    }

    private PermissionGrant? MatchGrant(PermissionRequest request)
    {
        if (_grantStore is null)
            return null;

        var grants = _grantStore.Lookup(request.SessionId, request.PermissionKind, request.Resource);
        if (grants.Count == 0)
            return null;

        // 安全优先：命中多条时 Deny 覆盖 Allow
        return grants.FirstOrDefault(g => g.Effect == PermissionEffect.Deny) ?? grants[0];
    }

    private async Task<AgentPermissionPolicy?> ResolveAgentPolicyAsync(PermissionRequest request, CancellationToken ct)
    {
        if (_agentRegistry is null || string.IsNullOrWhiteSpace(request.AgentName))
            return null;

        try
        {
            var definition = await _agentRegistry.GetAgentAsync(request.AgentName!).ConfigureAwait(false);
            return definition?.BuildPermissionPolicy();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "解析 Agent 权限策略失败: {Agent}", request.AgentName);
            return null;
        }
    }

    private static PermissionContext BuildPolicyContext(PermissionRequest request, AgentPermissionPolicy policy) => new()
    {
        SessionId = request.SessionId,
        AgentName = request.AgentName ?? string.Empty,
        Policy = policy,
        WorkingDirectory = Directory.GetCurrentDirectory()
    };

    private async Task PresentAsync(PermissionRequest request, CancellationToken ct)
    {
        foreach (var channel in _channels)
        {
            try
            {
                await channel.PresentAsync(request, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "宿主权限通道呈现失败: {Channel}", channel.GetType().Name);
            }
        }
    }

    private static bool IsFilesystemKind(string? permissionKind) =>
        permissionKind?.StartsWith("filesystem.", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// 资源类 kind（filesystem.* / shell.* / network.* / mcp.*）：其 Agent 规则 Allow 不短路，须走审批。
    /// <para>
    /// <c>mcp.*</c> 纳入资源类：MCP 工具以 <c>mcp.execute</c> + resource=server 名发起 server 粒度审批，
    /// 否则 build 的 <c>Allow(Tool,"*")</c> 会在步骤 3b 短路放行，导致 MCP 工具零审批。
    /// </para>
    /// </summary>
    private static bool IsResourceKind(string? permissionKind) =>
        IsFilesystemKind(permissionKind) ||
        permissionKind?.StartsWith("shell.", StringComparison.OrdinalIgnoreCase) == true ||
        permissionKind?.StartsWith("network.", StringComparison.OrdinalIgnoreCase) == true ||
        permissionKind?.StartsWith("mcp.", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>取资源所在目录（<c>Path.GetFullPath</c> → <c>GetDirectoryName</c>）；失败返回 null。</summary>
    private string? ResolveResourceDirectory(string resource)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(resource));
            return string.IsNullOrEmpty(directory) ? null : directory;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "解析资源目录失败: {Resource}", resource);
            return null;
        }
    }

    private static PermissionResolution Result(
        PermissionRequest request,
        PermissionEffect decision,
        PermissionResolvedBy resolvedBy,
        string? reason,
        PermissionGrantScope scope = PermissionGrantScope.Once) => new()
        {
            RequestId = request.RequestId!,
            SessionId = request.SessionId,
            CallId = request.CallId,
            Decision = decision,
            Scope = scope,
            ResolvedBy = resolvedBy,
            Reason = reason
        };

    private async Task AuditAsync(PermissionRequest request, PermissionResolution resolution, CancellationToken ct)
    {
        try
        {
            var result = new PermissionResult
            {
                Effect = resolution.Decision,
                Resource = new ResourceIdentifier(
                    PermissionKindMapper.Map(request.PermissionKind),
                    request.Resource ?? string.Empty),
                Reason = resolution.Reason ?? string.Empty
            };

            var context = new PermissionContext
            {
                SessionId = request.SessionId,
                AgentName = request.AgentName ?? string.Empty
            };

            await LogAuditAsync(result, context, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "权限审计失败: {RequestId}", request.RequestId);
        }
    }

    #endregion

    #region Private Methods

    private async Task<PermissionResult> EvaluateResourceAsync(
        ResourceIdentifier resource,
        PermissionContext context,
        CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var evaluationPath = new List<PermissionEvaluationStep>();

        try
        {
            // 1. 验证 Context 完整性
            var integrityHash = PermissionIntegrity.ComputeIntegrityHash(context, _hmacKey);
            var stepResult = await RecordStepAsync("ValidateIntegrity", resource, context,
                () => Task.FromResult(true), stopwatch.ElapsedMilliseconds).ConfigureAwait(false);
            evaluationPath.Add(stepResult);

            // 2. 评估规则（按优先级）
            var (matchedRule, effect, reason) = await EvaluateRulesAsync(resource, context, cancellationToken).ConfigureAwait(false);

            // 3. 检查父上下文（递归）- 父上下文可以覆盖当前决策
            if (context.Parent != null)
            {
                var parentResult = await EvaluateResourceAsync(resource, context.Parent, cancellationToken).ConfigureAwait(false);

                // 父上下文 Deny 总是覆盖当前 Allow
                if (parentResult.IsDenied)
                {
                    effect = PermissionEffect.Deny;
                    reason = $"Denied by parent context: {parentResult.Reason}";
                    matchedRule = parentResult.MatchedRule;
                }
                // 父上下文 Ask 时，如果当前是 Allow，降级为 Ask
                else if (parentResult.NeedsConfirmation && effect == PermissionEffect.Allow)
                {
                    effect = PermissionEffect.Ask;
                    reason = $"Parent requires confirmation: {parentResult.Reason}";
                }

                evaluationPath.Add(new PermissionEvaluationStep
                {
                    Step = "ParentContextCheck",
                    Input = context.Parent.AgentName,
                    Output = effect,
                    Matched = effect == PermissionEffect.Allow,
                    Duration = stopwatch.Elapsed
                });
            }

            // 4. 记录审计日志
            var result = CreateResult(effect, resource, reason, matchedRule, evaluationPath, integrityHash);
            await LogAuditAsync(result, context, cancellationToken).ConfigureAwait(false);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "权限评估失败: {Resource} for {Agent}", resource, context.AgentName);

            return CreateResult(
                PermissionEffect.Deny,
                resource,
                $"Evaluation failed: {ex.Message}",
                null,
                evaluationPath,
                string.Empty);
        }
    }

    private byte[] LoadOrGenerateHmacKey()
    {
        var keyPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Seeing.Agent", "permission_hmac_key.bin");

        if (File.Exists(keyPath))
        {
            try
            {
                return File.ReadAllBytes(keyPath);
            }
            catch (FileNotFoundException)
            {
                _logger?.LogInformation("HMAC 密钥文件在读取前被移除，将生成新密钥");
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger?.LogError(ex, "无权限读取 HMAC 密钥文件: {Path}", keyPath);
                throw;
            }
            catch (IOException ex)
            {
                _logger?.LogError(ex, "读取 HMAC 密钥文件 I/O 错误: {Path}", keyPath);
                throw;
            }
        }

        var key = new byte[32];
        RandomNumberGenerator.Fill(key);

        try
        {
            var dir = Path.GetDirectoryName(keyPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllBytes(keyPath, key);
            _logger?.LogInformation("已生成并保存新的 HMAC 密钥到: {Path}", keyPath);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "无法持久化 HMAC 密钥，每次启动将生成新密钥: {Path}", keyPath);
        }

        return key;
    }

    private async Task<(PermissionRuleEntry? Rule, PermissionEffect Effect, string Reason)> EvaluateRulesAsync(
        ResourceIdentifier resource,
        PermissionContext context,
        CancellationToken cancellationToken)
    {
        // 规则排序：优先级降序，相同优先级时 Deny 优先，最后按创建时间
        // 注意：不过滤 r.Kind == resource.Kind，因为 PermissionRuleEntry.Matches() 
        // 有跨类型匹配逻辑（如 PermissionKind.Tool 规则可匹配 McpTool 和 Skill）
        var rules = context.Policy.Rules
            .OrderByDescending(r => r.Priority)
            .ThenBy(r => r.Effect == PermissionEffect.Allow ? 1 : 0) // Deny (0) 优先于 Allow (1)
            .ThenBy(r => r.CreatedAt)
            .ToList();

        // 检查禁止的工具列表
        if (resource.Kind == PermissionKind.Tool &&
            context.Policy.DeniedTools.Contains(resource.Name, StringComparer.OrdinalIgnoreCase))
        {
            return (null, PermissionEffect.Deny, $"Tool '{resource.Name}' is in denied list");
        }

        // 检查允许的工具列表
        if (resource.Kind == PermissionKind.Tool &&
            context.Policy.AllowedTools.Count > 0 &&
            !context.Policy.AllowedTools.Contains(resource.Name, StringComparer.OrdinalIgnoreCase))
        {
            return (null, PermissionEffect.Deny, $"Tool '{resource.Name}' is not in allowed list");
        }

        // 评估规则
        foreach (var rule in rules)
        {
            if (rule.Matches(resource))
            {
                // 检查条件
                if (rule.Conditions != null)
                {
                    var conditionMet = EvaluateConditions(rule.Conditions, context);
                    if (!conditionMet)
                    {
                        continue;
                    }
                }

                var reason = rule.Effect == PermissionEffect.Allow
                    ? $"Allowed by rule {rule.Id} from {rule.Source}"
                    : rule.Effect == PermissionEffect.Deny
                        ? $"Denied by rule {rule.Id} from {rule.Source}"
                        : $"Requires confirmation by rule {rule.Id} from {rule.Source}";

                return (rule, rule.Effect, reason);
            }
        }

        // 没有匹配的规则，使用默认效果
        return (null, context.Policy.DefaultEffect, $"No matching rule, using default: {context.Policy.DefaultEffect}");
    }

    private bool EvaluateConditions(PermissionConditionSet conditionSet, PermissionContext context)
    {
        if (conditionSet.Conditions.Count == 0)
            return true;

        var results = conditionSet.Conditions.Select(c => EvaluateCondition(c, context)).ToList();

        return conditionSet.Logic == ConditionLogic.And
            ? results.All(r => r)
            : results.Any(r => r);
    }

    private bool EvaluateCondition(PermissionCondition condition, PermissionContext context)
    {
        var value = condition.Key switch
        {
            "SessionId" => context.SessionId,
            "AgentName" => context.AgentName,
            "WorkingDirectory" => context.WorkingDirectory,
            _ => context.EnvironmentSnapshot.TryGetValue(condition.Key, out var envValue) ? envValue : null
        };

        if (value == null && condition.Value != null)
            return condition.Operator == ConditionOperator.NotEquals;

        return condition.Operator switch
        {
            ConditionOperator.Equals => string.Equals(value?.ToString(), condition.Value?.ToString(), StringComparison.OrdinalIgnoreCase),
            ConditionOperator.NotEquals => !string.Equals(value?.ToString(), condition.Value?.ToString(), StringComparison.OrdinalIgnoreCase),
            ConditionOperator.Contains => value?.ToString()?.Contains(condition.Value?.ToString() ?? string.Empty) ?? false,
            ConditionOperator.NotContains => !(value?.ToString()?.Contains(condition.Value?.ToString() ?? string.Empty) ?? true),
            ConditionOperator.StartsWith => value?.ToString()?.StartsWith(condition.Value?.ToString() ?? string.Empty) ?? false,
            ConditionOperator.EndsWith => value?.ToString()?.EndsWith(condition.Value?.ToString() ?? string.Empty) ?? false,
            ConditionOperator.Matches => System.Text.RegularExpressions.Regex.IsMatch(value?.ToString() ?? string.Empty, condition.Value?.ToString() ?? string.Empty),
            ConditionOperator.FileExists => File.Exists(condition.Value?.ToString() ?? string.Empty),
            ConditionOperator.DirectoryExists => Directory.Exists(condition.Value?.ToString() ?? string.Empty),
            ConditionOperator.IsSubPathOf => IsSubPathOf(value?.ToString(), condition.Value?.ToString()),
            _ => false
        };
    }

    private static bool IsSubPathOf(string? path, string? parentPath)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(parentPath))
            return false;

        return PathSafety.IsPathWithinDirectory(path, parentPath);
    }

    private static PermissionResult CreateResult(
        PermissionEffect effect,
        ResourceIdentifier resource,
        string reason,
        PermissionRuleEntry? matchedRule,
        IReadOnlyList<PermissionEvaluationStep> evaluationPath,
        string contextHash)
    {
        return new PermissionResult
        {
            Effect = effect,
            Resource = resource,
            Reason = reason,
            MatchedRule = matchedRule,
            EvaluationPath = evaluationPath,
            ContextHash = contextHash,
            EvaluatedAt = DateTimeOffset.Now
        };
    }

    private static async Task<PermissionEvaluationStep> RecordStepAsync<T>(
        string stepName,
        object input,
        object context,
        Func<Task<T>> execute,
        long elapsedMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await execute().ConfigureAwait(false);
            return new PermissionEvaluationStep
            {
                Step = stepName,
                Input = input,
                Output = result,
                Matched = true,
                Duration = sw.Elapsed
            };
        }
        catch (Exception ex)
        {
            return new PermissionEvaluationStep
            {
                Step = stepName,
                Input = input,
                Output = ex.Message,
                Matched = false,
                Duration = sw.Elapsed
            };
        }
    }

    #endregion
}
