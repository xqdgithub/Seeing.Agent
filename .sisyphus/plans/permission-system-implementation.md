# Permission System Implementation Plan

## Overview

本文档定义了权限系统重构的完整实施计划，包含所有代码实现细节。

**Status**: In Progress  
**Created**: 2025-01-18  
**Architecture Review**: ✅ Passed

---

## Phase 1: Core Models

### 1.1 PermissionKind.cs

**Path**: `Core/Permission/Models/PermissionKind.cs`

```csharp
namespace Seeing.Agent.Core.Permission;

/// <summary>
/// 权限类型 - 细粒度资源分类
/// </summary>
public enum PermissionKind
{
    /// <summary>工具调用权限 - 内置工具</summary>
    Tool = 0,
    
    /// <summary>子代理调用权限 - 调用其他 Agent</summary>
    Agent = 1,
    
    /// <summary>文件系统权限 - 文件读写操作</summary>
    File = 2,
    
    /// <summary>网络请求权限 - HTTP/WebSocket 请求</summary>
    Network = 3,
    
    /// <summary>MCP 工具权限 - Model Context Protocol 工具</summary>
    McpTool = 4,
    
    /// <summary>技能执行权限 - Skill 调用</summary>
    Skill = 5,
    
    /// <summary>Shell 命令权限 - 命令行执行</summary>
    Shell = 6,
    
    /// <summary>环境变量访问权限 - 读取/设置环境变量</summary>
    Environment = 7
}

/// <summary>
/// 权限效果 - 权限规则的判定结果
/// </summary>
public enum PermissionEffect
{
    Allow = 0,
    Deny = 1,
    Ask = 2
}

/// <summary>
/// 条件运算符
/// </summary>
public enum ConditionOperator
{
    Equals = 0,
    NotEquals = 1,
    Contains = 2,
    NotContains = 3,
    StartsWith = 4,
    EndsWith = 5,
    Matches = 6,
    GreaterThan = 7,
    LessThan = 8,
    InRange = 9,
    FileExists = 10,
    DirectoryExists = 11,
    IsSubPathOf = 12
}

/// <summary>
/// 条件组合逻辑
/// </summary>
public enum ConditionLogic
{
    And = 0,
    Or = 1
}

/// <summary>
/// 文件操作类型
/// </summary>
public enum FileOperation
{
    Read = 0,
    Write = 1,
    Delete = 2,
    Execute = 3,
    List = 4
}
```

### 1.2 ResourceIdentifier.cs

**Path**: `Core/Permission/Models/ResourceIdentifier.cs`

```csharp
namespace Seeing.Agent.Core.Permission;

/// <summary>
/// 资源标识符 - 统一的资源命名格式
/// </summary>
/// <remarks>
/// 格式: kind:[namespace:]name
/// 示例: tool:bash, mcp:filesystem:read_file, agent:oracle
/// </remarks>
public readonly struct ResourceIdentifier : IEquatable<ResourceIdentifier>
{
    public PermissionKind Kind { get; }
    public string Namespace { get; }
    public string Name { get; }
    
    public ResourceIdentifier(PermissionKind kind, string name, string? ns = null)
    {
        Kind = kind;
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Namespace = ns ?? string.Empty;
    }
    
    public static ResourceIdentifier Parse(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            throw new ArgumentNullException(nameof(identifier));
        
        var parts = identifier.Split(':');
        
        return parts.Length switch
        {
            2 => new ResourceIdentifier(
                Enum.Parse<PermissionKind>(parts[0], ignoreCase: true),
                parts[1]),
            3 => new ResourceIdentifier(
                Enum.Parse<PermissionKind>(parts[0], ignoreCase: true),
                parts[2],
                parts[1]),
            _ => throw new FormatException(
                $"Invalid resource identifier format: {identifier}. " +
                $"Expected 'kind:name' or 'kind:namespace:name'")
        };
    }
    
    public static bool TryParse(string identifier, out ResourceIdentifier result)
    {
        try
        {
            result = Parse(identifier);
            return true;
        }
        catch
        {
            result = default;
            return false;
        }
    }
    
    public string ToCanonicalString() =>
        string.IsNullOrEmpty(Namespace)
            ? $"{Kind.ToString().ToLowerInvariant()}:{Name}"
            : $"{Kind.ToString().ToLowerInvariant()}:{Namespace}:{Name}";
    
    #region IEquatable
    public bool Equals(ResourceIdentifier other) =>
        Kind == other.Kind && Namespace == other.Namespace && Name == other.Name;
    
    public override bool Equals(object? obj) => obj is ResourceIdentifier other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Kind, Namespace, Name);
    public static bool operator ==(ResourceIdentifier left, ResourceIdentifier right) => left.Equals(right);
    public static bool operator !=(ResourceIdentifier left, ResourceIdentifier right) => !left.Equals(right);
    #endregion
    
    public override string ToString() => ToCanonicalString();
    public static implicit operator string(ResourceIdentifier identifier) => identifier.ToCanonicalString();
}
```

### 1.3 PermissionRuleEntry.cs

**Path**: `Core/Permission/Models/PermissionRuleEntry.cs`

```csharp
namespace Seeing.Agent.Core.Permission;

/// <summary>
/// 权限规则条目 - 新权限系统的规则定义
/// </summary>
public sealed class PermissionRuleEntry
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public PermissionKind Kind { get; init; }
    public string Pattern { get; init; } = "*";
    public string? Namespace { get; init; }
    public PermissionEffect Effect { get; init; } = PermissionEffect.Deny;
    public PermissionConditionSet? Conditions { get; init; }
    public int Priority { get; init; }
    public string Source { get; init; } = "builtin";
    public bool Delegable { get; init; } = true;
    public TimeSpan? TimeToLive { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? Description { get; init; }
    
    public bool Matches(ResourceIdentifier resource)
    {
        if (resource.Kind != Kind) return false;
        if (!string.IsNullOrEmpty(Namespace) && resource.Namespace != Namespace) return false;
        return WildcardMatch(Pattern, resource.Name);
    }
    
    public static bool WildcardMatch(string pattern, string input)
    {
        if (string.IsNullOrEmpty(pattern)) return string.IsNullOrEmpty(input);
        if (pattern == "*") return true;
        if (string.IsNullOrEmpty(input)) return false;
        
        int patternIndex = 0, inputIndex = 0, starIndex = -1, matchIndex = 0;
        
        while (inputIndex < input.Length)
        {
            if (patternIndex < pattern.Length &&
                (pattern[patternIndex] == input[inputIndex] || pattern[patternIndex] == '?'))
            {
                patternIndex++; inputIndex++;
            }
            else if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                starIndex = patternIndex;
                matchIndex = inputIndex;
                patternIndex++;
            }
            else if (starIndex != -1)
            {
                patternIndex = starIndex + 1;
                matchIndex++;
                inputIndex = matchIndex;
            }
            else return false;
        }
        
        while (patternIndex < pattern.Length && pattern[patternIndex] == '*') patternIndex++;
        return patternIndex == pattern.Length;
    }
    
    public static PermissionRuleEntry Allow(PermissionKind kind, string pattern, int priority = 0, string? ns = null)
        => new() { Kind = kind, Pattern = pattern, Namespace = ns, Effect = PermissionEffect.Allow, Priority = priority };
    
    public static PermissionRuleEntry Deny(PermissionKind kind, string pattern, int priority = 100, string? ns = null)
        => new() { Kind = kind, Pattern = pattern, Namespace = ns, Effect = PermissionEffect.Deny, Priority = priority };
    
    public override string ToString() => $"[{Id}] {Kind}:{Pattern} -> {Effect} (P{Priority})";
}

public sealed record PermissionCondition
{
    public string Key { get; init; } = string.Empty;
    public object? Value { get; init; }
    public ConditionOperator Operator { get; init; } = ConditionOperator.Equals;
    public string? Description { get; init; }
}

public sealed record PermissionConditionSet
{
    public IReadOnlyList<PermissionCondition> Conditions { get; init; } = Array.Empty<PermissionCondition>();
    public ConditionLogic Logic { get; init; } = ConditionLogic.And;
    
    public static PermissionConditionSet And(params PermissionCondition[] conditions)
        => new() { Conditions = conditions.ToList(), Logic = ConditionLogic.And };
    
    public static PermissionConditionSet Or(params PermissionCondition[] conditions)
        => new() { Conditions = conditions.ToList(), Logic = ConditionLogic.Or };
}
```

### 1.4 PermissionContext.cs

**Path**: `Core/Permission/Models/PermissionContext.cs`

```csharp
using System.Security.Cryptography;
using System.Text.Json;

namespace Seeing.Agent.Core.Permission;

/// <summary>
/// 权限上下文 - 包含完整性保护的权限评估上下文
/// </summary>
public sealed class PermissionContext
{
    private readonly byte[] _hmacKey;
    private string? _cachedIntegrityHash;
    
    public string SessionId { get; init; } = string.Empty;
    public string AgentName { get; init; } = string.Empty;
    public PermissionContext? Parent { get; init; }
    public AgentPermissionPolicy Policy { get; init; } = AgentPermissionPolicy.Empty;
    public IReadOnlyDictionary<string, string> EnvironmentSnapshot { get; init; } = new Dictionary<string, string>();
    public string WorkingDirectory { get; init; } = Directory.GetCurrentDirectory();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string Nonce { get; init; } = Guid.NewGuid().ToString("N");
    
    public PermissionContext(byte[]? hmacKey = null)
    {
        _hmacKey = hmacKey ?? GenerateHmacKey();
    }
    
    public string ComputeIntegrityHash()
    {
        if (_cachedIntegrityHash != null) return _cachedIntegrityHash;
        
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            SessionId,
            AgentName,
            PolicyId = Policy.Id,
            PolicyHash = Policy.ContentHash,
            WorkingDirectory,
            Timestamp = Timestamp.ToUnixTimeMilliseconds(),
            Nonce,
            ParentHash = Parent?.ComputeIntegrityHash()
        });
        
        using var hmac = new HMACSHA256(_hmacKey);
        var hash = hmac.ComputeHash(payload);
        _cachedIntegrityHash = Convert.ToBase64String(hash);
        
        return _cachedIntegrityHash;
    }
    
    public bool VerifyIntegrity(string expectedHash) => ComputeIntegrityHash() == expectedHash;
    
    public PermissionContext CreateSubAgentContext(string subAgentName, AgentPermissionPolicy subPolicy)
    {
        if (!Policy.IsDelegableTo(subAgentName))
            throw new PermissionDelegationException($"Agent '{AgentName}' cannot delegate to '{subAgentName}'");
        
        var mergedPolicy = Policy.Intersect(subPolicy);
        
        return new PermissionContext(_hmacKey)
        {
            SessionId = SessionId,
            AgentName = subAgentName,
            Parent = this,
            Policy = mergedPolicy,
            EnvironmentSnapshot = EnvironmentSnapshot,
            WorkingDirectory = WorkingDirectory
        };
    }
    
    private static byte[] GenerateHmacKey()
    {
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        return key;
    }
    
    public static PermissionContext FromAgentContext(
        Seeing.Agent.Core.Models.AgentContext agentContext,
        AgentPermissionPolicy policy,
        byte[]? hmacKey = null)
    {
        return new PermissionContext(hmacKey)
        {
            SessionId = agentContext.SessionId,
            AgentName = "unknown", // agentContext.Agent?.Name ?? "unknown",
            Policy = policy,
            EnvironmentSnapshot = CaptureEnvironment(),
            WorkingDirectory = agentContext.WorkingDirectory
        };
    }
    
    private static IReadOnlyDictionary<string, string> CaptureEnvironment()
    {
        var whitelist = new[] { "PATH", "HOME", "USER", "TEMP", "TMP", "PWD" };
        var result = new Dictionary<string, string>();
        foreach (var key in whitelist)
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (value != null) result[key] = value;
        }
        return result;
    }
}

public class PermissionDelegationException : Exception
{
    public PermissionDelegationException(string message) : base(message) { }
}
```

### 1.5 AgentPermissionPolicy.cs

**Path**: `Core/Permission/Models/AgentPermissionPolicy.cs`

```csharp
using System.Security.Cryptography;
using System.Text.Json;

namespace Seeing.Agent.Core.Permission;

/// <summary>
/// Agent 权限策略 - 完整的策略定义
/// </summary>
public sealed class AgentPermissionPolicy
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string AgentName { get; init; } = string.Empty;
    public int Version { get; init; } = 1;
    public IReadOnlyList<PermissionRuleEntry> Rules { get; init; } = Array.Empty<PermissionRuleEntry>();
    public IReadOnlyList<string> AllowedTools { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> DeniedTools { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AllowedAgents { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AllowedMcpServers { get; init; } = Array.Empty<string>();
    public PermissionEffect DefaultEffect { get; init; } = PermissionEffect.Deny;
    public string? Signature { get; private set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string ContentHash { get; private set; } = string.Empty;
    
    public static readonly AgentPermissionPolicy Empty = new()
    {
        DefaultEffect = PermissionEffect.Deny,
        ContentHash = ComputeHash(Array.Empty<PermissionRuleEntry>())
    };
    
    public static readonly AgentPermissionPolicy Permissive = new()
    {
        DefaultEffect = PermissionEffect.Allow,
        Rules = new[] { PermissionRuleEntry.Allow(PermissionKind.Tool, "*", 0) },
        ContentHash = ComputeHash(Array.Empty<PermissionRuleEntry>())
    };
    
    private static string ComputeHash(IReadOnlyList<PermissionRuleEntry> rules)
    {
        using var sha256 = SHA256.Create();
        var json = JsonSerializer.SerializeToUtf8Bytes(rules);
        var hash = sha256.ComputeHash(json);
        return Convert.ToBase64String(hash);
    }
    
    public void Sign(byte[] hmacKey)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { Id, AgentName, Version, Rules, DefaultEffect });
        using var hmac = new HMACSHA256(hmacKey);
        var signature = hmac.ComputeHash(payload);
        Signature = Convert.ToBase64String(signature);
        ContentHash = ComputeHash(Rules);
    }
    
    public bool VerifySignature(byte[] hmacKey)
    {
        if (string.IsNullOrEmpty(Signature)) return false;
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { Id, AgentName, Version, Rules, DefaultEffect });
        using var hmac = new HMACSHA256(hmacKey);
        var expected = Convert.ToBase64String(hmac.ComputeHash(payload));
        return Signature == expected;
    }
    
    public bool IsDelegableTo(string agentName)
    {
        if (AllowedAgents.Count == 0) return true;
        return AllowedAgents.Contains(agentName, StringComparer.OrdinalIgnoreCase);
    }
    
    public AgentPermissionPolicy Intersect(AgentPermissionPolicy other)
    {
        var mergedRules = new List<PermissionRuleEntry>();
        
        foreach (var kind in Enum.GetValues<PermissionKind>())
        {
            var thisRules = Rules.Where(r => r.Kind == kind).ToList();
            var otherRules = other.Rules.Where(r => r.Kind == kind).ToList();
            mergedRules.AddRange(MergeRuleSets(thisRules, otherRules, kind));
        }
        
        var mergedAllowedTools = AllowedTools.Count > 0 && other.AllowedTools.Count > 0
            ? AllowedTools.Intersect(other.AllowedTools, StringComparer.OrdinalIgnoreCase).ToList()
            : AllowedTools.Count > 0 ? AllowedTools : other.AllowedTools;
        
        var mergedDeniedTools = DeniedTools.Union(other.DeniedTools, StringComparer.OrdinalIgnoreCase).ToList();
        var mergedDefault = (PermissionEffect)Math.Max((int)DefaultEffect, (int)other.DefaultEffect);
        
        return new AgentPermissionPolicy
        {
            AgentName = $"{AgentName}∩{other.AgentName}",
            Rules = mergedRules,
            AllowedTools = mergedAllowedTools,
            DeniedTools = mergedDeniedTools,
            AllowedAgents = AllowedAgents.Intersect(other.AllowedAgents).ToList(),
            AllowedMcpServers = AllowedMcpServers.Intersect(other.AllowedMcpServers).ToList(),
            DefaultEffect = mergedDefault,
            ContentHash = ComputeHash(mergedRules)
        };
    }
    
    private static IEnumerable<PermissionRuleEntry> MergeRuleSets(
        List<PermissionRuleEntry> set1, List<PermissionRuleEntry> set2, PermissionKind kind)
    {
        var allow1 = set1.Where(r => r.Effect == PermissionEffect.Allow).ToList();
        var allow2 = set2.Where(r => r.Effect == PermissionEffect.Allow).ToList();
        
        if (allow1.Count > 0 && allow2.Count > 0)
        {
            foreach (var r1 in allow1)
            {
                foreach (var r2 in allow2)
                {
                    if (PatternsIntersect(r1.Pattern, r2.Pattern, out var intersection))
                    {
                        yield return new PermissionRuleEntry
                        {
                            Kind = kind,
                            Pattern = intersection,
                            Effect = PermissionEffect.Allow,
                            Priority = Math.Max(r1.Priority, r2.Priority),
                            Source = $"{r1.Source}∩{r2.Source}",
                            Delegable = r1.Delegable && r2.Delegable
                        };
                    }
                }
            }
        }
        else if (allow1.Count > 0) { foreach (var r in allow1) yield return r; }
        else if (allow2.Count > 0) { foreach (var r in allow2) yield return r; }
        
        foreach (var r in set1.Where(r => r.Effect == PermissionEffect.Deny)) yield return r;
        foreach (var r in set2.Where(r => r.Effect == PermissionEffect.Deny)) yield return r;
    }
    
    private static bool PatternsIntersect(string pattern1, string pattern2, out string intersection)
    {
        intersection = string.Empty;
        if (pattern1 == pattern2) { intersection = pattern1; return true; }
        if (pattern1 == "*") { intersection = pattern2; return true; }
        if (pattern2 == "*") { intersection = pattern1; return true; }
        
        if (pattern1.EndsWith("/*") && pattern2.EndsWith("/*"))
        {
            var prefix1 = pattern1[..^2];
            var prefix2 = pattern2[..^2];
            if (prefix2.StartsWith(prefix1)) { intersection = pattern2; return true; }
            if (prefix1.StartsWith(prefix2)) { intersection = pattern1; return true; }
        }
        
        return false;
    }
}
```

### 1.6 PermissionResult.cs

**Path**: `Core/Permission/Models/PermissionResult.cs`

```csharp
namespace Seeing.Agent.Core.Permission;

/// <summary>
/// 权限评估结果 - 包含完整评估路径和审计信息
/// </summary>
public sealed class PermissionResult
{
    public PermissionEffect Effect { get; init; }
    public ResourceIdentifier Resource { get; init; }
    public string Reason { get; init; } = string.Empty;
    public PermissionRuleEntry? MatchedRule { get; init; }
    public IReadOnlyList<PermissionEvaluationStep> EvaluationPath { get; init; } = Array.Empty<PermissionEvaluationStep>();
    public string ContextHash { get; init; } = string.Empty;
    public DateTimeOffset EvaluatedAt { get; init; } = DateTimeOffset.UtcNow;
    public TimeSpan? CacheTtl { get; init; }
    public bool FromCache { get; init; }
    
    public bool IsAllowed => Effect == PermissionEffect.Allow;
    public bool IsDenied => Effect == PermissionEffect.Deny;
    public bool NeedsConfirmation => Effect == PermissionEffect.Ask;
    
    public static PermissionResult Allow(ResourceIdentifier resource, string reason, PermissionRuleEntry? rule = null, IReadOnlyList<PermissionEvaluationStep>? path = null)
        => new() { Effect = PermissionEffect.Allow, Resource = resource, Reason = reason, MatchedRule = rule, EvaluationPath = path ?? Array.Empty<PermissionEvaluationStep>() };
    
    public static PermissionResult Deny(ResourceIdentifier resource, string reason, PermissionRuleEntry? rule = null, IReadOnlyList<PermissionEvaluationStep>? path = null)
        => new() { Effect = PermissionEffect.Deny, Resource = resource, Reason = reason, MatchedRule = rule, EvaluationPath = path ?? Array.Empty<PermissionEvaluationStep>() };
    
    public static PermissionResult Ask(ResourceIdentifier resource, string reason, PermissionRuleEntry? rule = null, IReadOnlyList<PermissionEvaluationStep>? path = null)
        => new() { Effect = PermissionEffect.Ask, Resource = resource, Reason = reason, MatchedRule = rule, EvaluationPath = path ?? Array.Empty<PermissionEvaluationStep>() };
    
    public Seeing.Agent.Core.Interfaces.PermissionDecision ToDecision()
    {
        var action = Effect switch
        {
            PermissionEffect.Allow => Seeing.Agent.Core.Interfaces.PermissionAction.Allow,
            PermissionEffect.Deny => Seeing.Agent.Core.Interfaces.PermissionAction.Deny,
            PermissionEffect.Ask => Seeing.Agent.Core.Interfaces.PermissionAction.Ask,
            _ => Seeing.Agent.Core.Interfaces.PermissionAction.Deny
        };
        return new Seeing.Agent.Core.Interfaces.PermissionDecision(action, Reason, null);
    }
    
    public override string ToString() => $"[{Effect}] {Resource}: {Reason}";
}

/// <summary>
/// 权限评估步骤 - 记录评估过程
/// </summary>
public sealed class PermissionEvaluationStep
{
    public string Step { get; init; } = string.Empty;
    public object? Input { get; init; }
    public object? Output { get; init; }
    public bool Matched { get; init; }
    public TimeSpan Duration { get; init; }
    
    public override string ToString() => $"{Step}: {(Matched ? "✓" : "✗")} {Input} -> {Output}";
}
```

---

## Phase 2: Permission Service

### 2.1 IPermissionService.cs

**Path**: `Core/Permission/IPermissionService.cs`

```csharp
namespace Seeing.Agent.Core.Permission;

public interface IPermissionService
{
    Task<PermissionResult> EvaluateAsync(ResourceIdentifier resource, PermissionContext context, CancellationToken cancellationToken = default);
    Task<PermissionResult> EvaluateToolAsync(string toolName, string? ns, PermissionContext context, CancellationToken cancellationToken = default);
    Task<PermissionResult> EvaluateAgentAsync(string agentName, PermissionContext context, CancellationToken cancellationToken = default);
    Task<PermissionResult> EvaluateFileAsync(string filePath, FileOperation operation, PermissionContext context, CancellationToken cancellationToken = default);
    Task<PermissionResult> EvaluateMcpToolAsync(string mcpServer, string toolName, PermissionContext context, CancellationToken cancellationToken = default);
    Task<AgentPermissionPolicy> GetPolicyAsync(string agentName, CancellationToken cancellationToken = default);
    AgentPermissionPolicy MergePolicies(AgentPermissionPolicy global, AgentPermissionPolicy agent);
    void InvalidateCache(string? agentName = null, string? resourcePattern = null);
    Task LogAuditAsync(PermissionResult result, PermissionContext context, CancellationToken cancellationToken = default);
}
```

---

## Implementation Tasks

### Task 1: Create Model Files
- [x] Create `Core/Permission/Models/PermissionKind.cs`
- [x] Create `Core/Permission/Models/ResourceIdentifier.cs`
- [x] Create `Core/Permission/Models/PermissionRuleEntry.cs`
- [x] Create `Core/Permission/Models/PermissionContext.cs`
- [x] Create `Core/Permission/Models/AgentPermissionPolicy.cs`
- [x] Create `Core/Permission/Models/PermissionResult.cs`

### Task 2: Create Service Files
- [x] Create `Core/Permission/IPermissionService.cs`
- [x] Create `Core/Permission/PermissionService.cs`

### Task 3: Integration
- [x] Modify `AgentDefinition.cs` to add permission fields
- [x] Modify `AgentExecutor.cs` to integrate PermissionService
- [x] Create DI registration in `PermissionServiceExtensions.cs`

---

## Notes

- All models use `init` properties for immutability
- HMAC-SHA256 for integrity verification
- Strategy pattern for condition evaluators
- Backward compatible with existing `PermissionAction` and `PermissionDecision`
