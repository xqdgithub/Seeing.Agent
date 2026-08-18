namespace Seeing.Agent.Abstractions.Permissions;

/// <summary>
/// 权限上下文 - 包含完整性保护的权限评估上下文（属性 DTO 部分）
/// <para>
/// 完整性计算（HMAC-SHA256）与子代理上下文构建逻辑见主库
/// <c>Seeing.Agent.Core.Permission.PermissionIntegrity</c>。
/// </para>
/// </summary>
public sealed class PermissionContext
{
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
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    /// <summary>随机数（防重放）</summary>
    public string Nonce { get; init; } = Guid.NewGuid().ToString("N");
}