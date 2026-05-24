using System.Security.Cryptography;
using System.Text.Json;
using Seeing.Agent.Core.Models;

namespace Seeing.Agent.Core.Permission;

/// <summary>
/// 权限上下文 - 包含完整性保护的权限评估上下文
/// </summary>
public sealed class PermissionContext
{
    private readonly byte[] _hmacKey;
    private string? _cachedIntegrityHash;
    
    /// <summary>会话 ID</summary>
    public string SessionId { get; init; } = string.Empty;
    
    /// <summary>Agent 名称</summary>
    public string AgentName { get; init; } = string.Empty;
    
    /// <summary>父上下文（子代理调用时）</summary>
    public PermissionContext? Parent { get; init; }
    
    /// <summary>权限策略</summary>
    public AgentPermissionPolicy Policy { get; init; } = AgentPermissionPolicy.Empty;
    
    /// <summary>环境变量快照</summary>
    public IReadOnlyDictionary<string, string> EnvironmentSnapshot { get; init; } = new Dictionary<string, string>();
    
    /// <summary>工作目录</summary>
    public string WorkingDirectory { get; init; } = Directory.GetCurrentDirectory();
    
    /// <summary>时间戳</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    
    /// <summary>随机数（防重放）</summary>
    public string Nonce { get; init; } = Guid.NewGuid().ToString("N");
    
    /// <summary>
    /// 创建权限上下文
    /// </summary>
    /// <param name="hmacKey">HMAC 密钥（可选，自动生成）</param>
    public PermissionContext(byte[]? hmacKey = null)
    {
        _hmacKey = hmacKey ?? GenerateHmacKey();
    }
    
    /// <summary>
    /// 计算完整性哈希（HMAC-SHA256）
    /// </summary>
    /// <returns>Base64 编码的哈希值</returns>
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
    
    /// <summary>
    /// 验证完整性
    /// </summary>
    /// <param name="expectedHash">期望的哈希值</param>
    /// <returns>是否验证通过</returns>
    public bool VerifyIntegrity(string expectedHash) => ComputeIntegrityHash() == expectedHash;
    
    /// <summary>
    /// 创建子代理上下文
    /// </summary>
    /// <param name="subAgentName">子代理名称</param>
    /// <param name="subPolicy">子代理策略</param>
    /// <returns>新的权限上下文</returns>
    /// <exception cref="PermissionDelegationException">不允许委托</exception>
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
    
    /// <summary>
    /// 从 AgentContext 创建 PermissionContext
    /// </summary>
    /// <param name="agentContext">Agent 执行上下文</param>
    /// <param name="policy">权限策略</param>
    /// <param name="hmacKey">HMAC 密钥（可选）</param>
    /// <returns>权限上下文</returns>
    public static PermissionContext FromAgentContext(
        AgentContext agentContext,
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
