using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Core.Models;
using System.Security.Cryptography;
using System.Text.Json;

namespace Seeing.Agent.Core.Permission;

/// <summary>
/// 权限完整性工具 - PermissionContext 的 HMAC 完整性计算与子代理上下文构建逻辑
/// <para>
/// 零实现纪律：Abstractions 的 PermissionContext 仅保留属性 DTO，本类承载全部实现逻辑。
/// </para>
/// </summary>
internal static class PermissionIntegrity
{
    /// <summary>
    /// 生成 HMAC 密钥
    /// </summary>
    public static byte[] GenerateHmacKey()
    {
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        return key;
    }

    /// <summary>
    /// 计算完整性哈希（HMAC-SHA256）
    /// </summary>
    /// <param name="context">权限上下文</param>
    /// <param name="hmacKey">HMAC 密钥</param>
    /// <returns>Base64 编码的哈希值</returns>
    public static string ComputeIntegrityHash(PermissionContext context, byte[] hmacKey)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            context.SessionId,
            context.AgentName,
            PolicyId = context.Policy.Id,
            PolicyHash = context.Policy.ContentHash,
            context.WorkingDirectory,
            Timestamp = context.Timestamp.ToUnixTimeMilliseconds(),
            context.Nonce,
            ParentHash = context.Parent != null ? ComputeIntegrityHash(context.Parent, hmacKey) : null
        });

        using var hmac = new HMACSHA256(hmacKey);
        var hash = hmac.ComputeHash(payload);
        return Convert.ToBase64String(hash);
    }

    /// <summary>
    /// 验证完整性
    /// </summary>
    /// <param name="context">权限上下文</param>
    /// <param name="expectedHash">期望的哈希值</param>
    /// <param name="hmacKey">HMAC 密钥</param>
    /// <returns>是否验证通过</returns>
    public static bool VerifyIntegrity(PermissionContext context, string expectedHash, byte[] hmacKey)
        => ComputeIntegrityHash(context, hmacKey) == expectedHash;

    /// <summary>
    /// 创建子代理上下文
    /// </summary>
    /// <param name="context">父权限上下文</param>
    /// <param name="subAgentName">子代理名称</param>
    /// <param name="subPolicy">子代理策略</param>
    /// <param name="hmacKey">HMAC 密钥</param>
    /// <returns>新的权限上下文</returns>
    /// <exception cref="PermissionDelegationException">不允许委托</exception>
    public static PermissionContext CreateSubAgentContext(
        PermissionContext context,
        string subAgentName,
        AgentPermissionPolicy subPolicy,
        byte[] hmacKey)
    {
        if (!context.Policy.IsDelegableTo(subAgentName))
            throw new PermissionDelegationException($"Agent '{context.AgentName}' cannot delegate to '{subAgentName}'");

        var mergedPolicy = context.Policy.Intersect(subPolicy);

        return new PermissionContext
        {
            SessionId = context.SessionId,
            AgentName = subAgentName,
            Parent = context,
            Policy = mergedPolicy,
            EnvironmentSnapshot = context.EnvironmentSnapshot,
            WorkingDirectory = context.WorkingDirectory
        };
    }

    /// <summary>
    /// 从 AgentContext 创建 PermissionContext
    /// </summary>
    /// <param name="agentContext">Agent 执行上下文</param>
    /// <param name="policy">权限策略</param>
    /// <param name="agentName">Agent 名称（可选，用于审计日志）</param>
    /// <param name="hmacKey">HMAC 密钥（可选）</param>
    /// <returns>权限上下文</returns>
    public static PermissionContext FromAgentContext(
        AgentContext agentContext,
        AgentPermissionPolicy policy,
        string? agentName = null,
        byte[]? hmacKey = null)
    {
        return new PermissionContext
        {
            SessionId = agentContext.SessionId,
            AgentName = agentName ?? policy.AgentName ?? "unknown",
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

/// <summary>
/// 权限委托异常 - 表示不允许委托权限
/// </summary>
public class PermissionDelegationException : Exception
{
    /// <summary>
    /// 创建权限委托异常
    /// </summary>
    /// <param name="message">异常消息</param>
    public PermissionDelegationException(string message) : base(message) { }
}