using System.Security.Cryptography;
using System.Text.Json;

namespace Seeing.Agent.Abstractions.Permissions;

/// <summary>
/// Agent 权限策略 - 完整的策略定义
/// </summary>
public sealed class AgentPermissionPolicy
{
    /// <summary>策略唯一标识</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Agent 名称</summary>
    public string AgentName { get; init; } = string.Empty;

    /// <summary>策略版本</summary>
    public int Version { get; init; } = 1;

    /// <summary>权限规则列表</summary>
    public IReadOnlyList<PermissionRuleEntry> Rules { get; init; } = Array.Empty<PermissionRuleEntry>();

    /// <summary>允许的工具列表</summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = Array.Empty<string>();

    /// <summary>禁止的工具列表</summary>
    public IReadOnlyList<string> DeniedTools { get; init; } = Array.Empty<string>();

    /// <summary>允许的子代理列表</summary>
    public IReadOnlyList<string> AllowedAgents { get; init; } = Array.Empty<string>();

    /// <summary>允许的 MCP 服务器列表</summary>
    public IReadOnlyList<string> AllowedMcpServers { get; init; } = Array.Empty<string>();

    /// <summary>默认效果（当没有匹配规则时）</summary>
    public PermissionEffect DefaultEffect { get; init; } = PermissionEffect.Ask;

    /// <summary>策略签名</summary>
    public string? Signature { get; private set; }

    /// <summary>创建时间</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    /// <summary>内容哈希</summary>
    public string ContentHash { get; init; } = string.Empty;

    /// <summary>空策略（拒绝所有）</summary>
    public static readonly AgentPermissionPolicy Empty = new()
    {
        DefaultEffect = PermissionEffect.Deny,
        ContentHash = ComputeHash(Array.Empty<PermissionRuleEntry>())
    };

    /// <summary>宽松策略（允许所有）</summary>
    public static readonly AgentPermissionPolicy Permissive = new()
    {
        DefaultEffect = PermissionEffect.Allow,
        Rules = new[] { PermissionRuleEntry.Allow(PermissionKind.Tool, "*", 0) },
        ContentHash = ComputeHash(new[] { PermissionRuleEntry.Allow(PermissionKind.Tool, "*", 0) })
    };

    private static string ComputeHash(IReadOnlyList<PermissionRuleEntry> rules)
    {
        var json = JsonSerializer.Serialize(rules);
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json));
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>
    /// 签名策略
    /// </summary>
    /// <param name="hmacKey">HMAC 密钥</param>
    public void Sign(byte[] hmacKey)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { Id, AgentName, Version, Rules, DefaultEffect });
        using var hmac = new HMACSHA256(hmacKey);
        var signature = hmac.ComputeHash(payload);
        Signature = Convert.ToBase64String(signature);
    }

    /// <summary>
    /// 验证策略签名
    /// </summary>
    /// <param name="hmacKey">HMAC 密钥</param>
    /// <returns>签名是否有效</returns>
    public bool VerifySignature(byte[] hmacKey)
    {
        if (string.IsNullOrEmpty(Signature)) return false;
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { Id, AgentName, Version, Rules, DefaultEffect });
        using var hmac = new HMACSHA256(hmacKey);
        var expected = Convert.ToBase64String(hmac.ComputeHash(payload));
        return Signature == expected;
    }

    /// <summary>
    /// 检查是否可以委托给指定 Agent
    /// </summary>
    /// <param name="agentName">目标 Agent 名称</param>
    /// <returns>是否可委托</returns>
    public bool IsDelegableTo(string agentName)
    {
        if (AllowedAgents.Count == 0) return true;
        return AllowedAgents.Contains(agentName, StringComparer.OrdinalIgnoreCase);
    }
}
