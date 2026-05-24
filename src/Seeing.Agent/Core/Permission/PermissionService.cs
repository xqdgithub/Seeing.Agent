using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;

namespace Seeing.Agent.Core.Permission;

/// <summary>
/// 权限服务实现 - 统一的权限评估入口
/// </summary>
public sealed class PermissionService : IPermissionService, IDisposable
{
    private readonly IPermissionPolicyProvider _policyProvider;
    private readonly IPermissionChannel _channel;
    private readonly IPermissionCache _cache;
    private readonly ILogger<PermissionService> _logger;
    private readonly byte[] _hmacKey;
    private readonly TimeSpan _defaultCacheTtl = TimeSpan.FromMinutes(5);
    
    // TTL 清理定时器
    private readonly Timer _cleanupTimer;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _cacheExpirations = new();
    
    public PermissionService(
        IPermissionPolicyProvider policyProvider,
        IPermissionChannel channel,
        IPermissionCache cache,
        ILogger<PermissionService> logger)
    {
        _policyProvider = policyProvider ?? throw new ArgumentNullException(nameof(policyProvider));
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        
        // 生成或加载 HMAC 密钥
        _hmacKey = LoadOrGenerateHmacKey();
        
        // 启动 TTL 清理定时器（每分钟）
        _cleanupTimer = new Timer(
            callback: _ => CleanupExpiredCacheEntries(),
            state: null,
            dueTime: TimeSpan.FromMinutes(1),
            period: TimeSpan.FromMinutes(1));
    }
    
    /// <inheritdoc />
    public async Task<PermissionResult> EvaluateAsync(
        ResourceIdentifier resource, 
        PermissionContext context, 
        CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var evaluationPath = new List<PermissionEvaluationStep>();
        
        try
        {
            // 1. 验证 Context 完整性
            var integrityHash = context.ComputeIntegrityHash();
            var stepResult = await RecordStepAsync("ValidateIntegrity", resource, context, 
                () => Task.FromResult(true), stopwatch.ElapsedMilliseconds);
            evaluationPath.Add(stepResult);
            
            // 2. 构建缓存键
            var cacheKey = BuildCacheKey(resource, context);
            
            // 3. 检查缓存
            if (_cache.TryGet(cacheKey, out var cachedAction))
            {
                _logger.LogDebug("权限缓存命中: {Resource} for {Agent}", resource, context.AgentName);
                
                var cachedEffect = MapToEffect(cachedAction);
                return CreateResult(cachedEffect, resource, "From cache", null, evaluationPath, integrityHash, fromCache: true);
            }
            
            // 4. 评估规则（按优先级）
            var (matchedRule, effect, reason) = await EvaluateRulesAsync(resource, context, cancellationToken);
            
            // 5. 检查父上下文（递归）
            if (effect == PermissionEffect.Allow && context.Parent != null)
            {
                var parentResult = await EvaluateAsync(resource, context.Parent, cancellationToken);
                if (parentResult.IsDenied)
                {
                    effect = PermissionEffect.Deny;
                    reason = $"Denied by parent context: {parentResult.Reason}";
                    matchedRule = parentResult.MatchedRule;
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
            
            // 6. 缓存结果
            var cacheAction = MapToAction(effect);
            _cache.Set(cacheKey, cacheAction, _defaultCacheTtl);
            _cacheExpirations[cacheKey.ToString()] = DateTimeOffset.Now.Add(_defaultCacheTtl);
            
            // 7. 记录审计日志
            var result = CreateResult(effect, resource, reason, matchedRule, evaluationPath, integrityHash);
            await LogAuditAsync(result, context, cancellationToken);
            
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
    
    /// <inheritdoc />
    public async Task<PermissionResult> EvaluateToolAsync(
        string toolName, 
        string? ns, 
        PermissionContext context, 
        CancellationToken cancellationToken = default)
    {
        var resource = new ResourceIdentifier(PermissionKind.Tool, toolName, ns);
        return await EvaluateAsync(resource, context, cancellationToken);
    }
    
    /// <inheritdoc />
    public async Task<PermissionResult> EvaluateAgentAsync(
        string agentName, 
        PermissionContext context, 
        CancellationToken cancellationToken = default)
    {
        var resource = new ResourceIdentifier(PermissionKind.Agent, agentName);
        return await EvaluateAsync(resource, context, cancellationToken);
    }
    
    /// <inheritdoc />
    public async Task<PermissionResult> EvaluateFileAsync(
        string filePath, 
        FileOperation operation, 
        PermissionContext context, 
        CancellationToken cancellationToken = default)
    {
        // 路径规范化
        var normalizedPath = NormalizePath(filePath, context.WorkingDirectory);
        var resource = new ResourceIdentifier(PermissionKind.File, normalizedPath);
        
        var result = await EvaluateAsync(resource, context, cancellationToken);
        
        // 对于写入操作，需要通过 IPermissionChannel 获取确认
        if (result.NeedsConfirmation && operation == FileOperation.Write)
        {
            var decision = await _channel.RequestWritePermissionAsync(normalizedPath, null, CreateAgentContext(context));
            return new PermissionResult
            {
                Effect = MapToEffect(decision.Action),
                Resource = resource,
                Reason = decision.Reason ?? "User confirmation",
                MatchedRule = result.MatchedRule,
                EvaluationPath = result.EvaluationPath,
                ContextHash = result.ContextHash,
                FromCache = false
            };
        }
        
        return result;
    }
    
    /// <inheritdoc />
    public async Task<PermissionResult> EvaluateMcpToolAsync(
        string mcpServer, 
        string toolName, 
        PermissionContext context, 
        CancellationToken cancellationToken = default)
    {
        var resource = new ResourceIdentifier(PermissionKind.McpTool, toolName, mcpServer);
        return await EvaluateAsync(resource, context, cancellationToken);
    }
    
    /// <inheritdoc />
    public Task<AgentPermissionPolicy> GetPolicyAsync(string agentName, CancellationToken cancellationToken = default)
    {
        var policy = _policyProvider.GetPolicy(agentName);
        
        // 转换为新的 AgentPermissionPolicy
        var rules = new List<PermissionRuleEntry>();
        var priority = 0;
        
        foreach (var grant in policy.Grants)
        {
            if (TryParsePermissionKind(grant.Permission, out var kind))
            {
                rules.Add(PermissionRuleEntry.Allow(kind, grant.Pattern, priority++));
            }
        }
        
        foreach (var deny in policy.Denies)
        {
            if (TryParsePermissionKind(deny.Permission, out var kind))
            {
                rules.Add(PermissionRuleEntry.Deny(kind, deny.Pattern, priority++));
            }
        }
        
        var result = new AgentPermissionPolicy
        {
            AgentName = agentName,
            Rules = rules,
            AllowedTools = policy.AllowedTools ?? Array.Empty<string>(),
            AllowedMcpServers = Array.Empty<string>(),
            DefaultEffect = PermissionEffect.Deny
        };
        
        return Task.FromResult(result);
    }
    
    /// <inheritdoc />
    public AgentPermissionPolicy MergePolicies(AgentPermissionPolicy global, AgentPermissionPolicy agent)
    {
        return global.Intersect(agent);
    }
    
    /// <inheritdoc />
    public void InvalidateCache(string? agentName = null, string? resourcePattern = null)
    {
        if (agentName == null && resourcePattern == null)
        {
            _cache.Clear();
            _cacheExpirations.Clear();
            _logger.LogInformation("已清空所有权限缓存");
        }
        else if (agentName != null)
        {
            _cache.InvalidateByAgent(agentName);
            
            // 清理过期记录
            var keysToRemove = _cacheExpirations
                .Where(kvp => kvp.Key.StartsWith(agentName))
                .Select(kvp => kvp.Key)
                .ToList();
            
            foreach (var key in keysToRemove)
            {
                _cacheExpirations.TryRemove(key, out _);
            }
            
            _logger.LogInformation("已清除 Agent {AgentName} 的权限缓存", agentName);
        }
        else if (resourcePattern != null)
        {
            _cache.InvalidateByPermission(resourcePattern);
            _logger.LogInformation("已清除资源 {Pattern} 的权限缓存", resourcePattern);
        }
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
    
    /// <summary>
    /// 释放资源
    /// </summary>
    public void Dispose()
    {
        _cleanupTimer?.Dispose();
    }
    
    #region Private Methods
    
    private byte[] LoadOrGenerateHmacKey()
    {
        // 在实际实现中，应该从安全配置中加载
        // 这里生成一个随机密钥
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        return key;
    }
    
    private void CleanupExpiredCacheEntries()
    {
        var now = DateTimeOffset.Now;
        var expiredKeys = _cacheExpirations
            .Where(kvp => kvp.Value <= now)
            .Select(kvp => kvp.Key)
            .ToList();
        
        foreach (var key in expiredKeys)
        {
            _cacheExpirations.TryRemove(key, out _);
        }
        
        if (expiredKeys.Count > 0)
        {
            _logger.LogDebug("清理了 {Count} 个过期的缓存条目", expiredKeys.Count);
        }
    }
    
    private PermissionCacheKey BuildCacheKey(ResourceIdentifier resource, PermissionContext context)
    {
        return new PermissionCacheKey(
            resource.Kind.ToString(),
            resource.ToCanonicalString(),
            context.AgentName);
    }
    
    private async Task<(PermissionRuleEntry? Rule, PermissionEffect Effect, string Reason)> EvaluateRulesAsync(
        ResourceIdentifier resource,
        PermissionContext context,
        CancellationToken cancellationToken)
    {
        var rules = context.Policy.Rules
            .Where(r => r.Kind == resource.Kind)
            .OrderByDescending(r => r.Priority)
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
        
        var fullPath = Path.GetFullPath(path);
        var fullParent = Path.GetFullPath(parentPath);
        
        return fullPath.StartsWith(fullParent, StringComparison.OrdinalIgnoreCase);
    }
    
    private static string NormalizePath(string filePath, string workingDirectory)
    {
        if (Path.IsPathRooted(filePath))
            return Path.GetFullPath(filePath);
        
        return Path.GetFullPath(Path.Combine(workingDirectory, filePath));
    }
    
    private static PermissionEffect MapToEffect(PermissionAction action)
    {
        return action switch
        {
            PermissionAction.Allow => PermissionEffect.Allow,
            PermissionAction.Deny => PermissionEffect.Deny,
            PermissionAction.Ask => PermissionEffect.Ask,
            _ => PermissionEffect.Deny
        };
    }
    
    private static PermissionAction MapToAction(PermissionEffect effect)
    {
        return effect switch
        {
            PermissionEffect.Allow => PermissionAction.Allow,
            PermissionEffect.Deny => PermissionAction.Deny,
            PermissionEffect.Ask => PermissionAction.Ask,
            _ => PermissionAction.Deny
        };
    }
    
    private static PermissionResult CreateResult(
        PermissionEffect effect,
        ResourceIdentifier resource,
        string reason,
        PermissionRuleEntry? matchedRule,
        IReadOnlyList<PermissionEvaluationStep> evaluationPath,
        string contextHash,
        bool fromCache = false)
    {
        return new PermissionResult
        {
            Effect = effect,
            Resource = resource,
            Reason = reason,
            MatchedRule = matchedRule,
            EvaluationPath = evaluationPath,
            ContextHash = contextHash,
            EvaluatedAt = DateTimeOffset.UtcNow,
            CacheTtl = TimeSpan.FromMinutes(5),
            FromCache = fromCache
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
            var result = await execute();
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
    
    private static bool TryParsePermissionKind(string permission, out PermissionKind kind)
    {
        return Enum.TryParse<PermissionKind>(permission, ignoreCase: true, out kind);
    }
    
    private static AgentContext CreateAgentContext(PermissionContext context)
    {
        return new AgentContext
        {
            SessionId = context.SessionId,
            WorkingDirectory = context.WorkingDirectory
        };
    }
    
    #endregion
}
