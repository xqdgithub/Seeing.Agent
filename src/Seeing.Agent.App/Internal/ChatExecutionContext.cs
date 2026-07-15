using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Core.Permission;
using Seeing.Agent.Llm;

namespace Seeing.Agent.App.Internal;

/// <summary>
/// 聊天执行上下文 - 内部使用的完整执行上下文
/// <para>
/// 从公开接口的最小参数构建，包含执行所需的所有信息。
/// </para>
/// </summary>
internal class ChatExecutionContext
{
    // ========== 会话相关 ==========
    
    /// <summary>会话 ID</summary>
    public required string SessionId { get; init; }
    
    /// <summary>对话历史</summary>
    public required List<ChatMessage> History { get; set; }
    
    // ========== Agent 相关 ==========
    
    /// <summary>Agent 定义</summary>
    public required AgentDefinition Agent { get; init; }
    
    /// <summary>工作目录</summary>
    public string? WorkingDirectory { get; init; }
    
    /// <summary>工作区根目录</summary>
    public string? WorkspaceRoot { get; init; }
    
    // ========== 权限相关 ==========
    
    /// <summary>权限通道</summary>
    public IPermissionChannel? PermissionChannel { get; init; }
    
    /// <summary>权限上下文</summary>
    public PermissionContext? PermissionContext { get; init; }
    
    // ========== 子代理相关 ==========
    
    /// <summary>父会话 ID（子代理调用时设置）</summary>
    public string? ParentSessionId { get; init; }
    
    /// <summary>父代理名称</summary>
    public string? ParentAgentName { get; init; }
    
    /// <summary>是否为顶层调用（Hook 触发控制）</summary>
    public bool IsTopLevel { get; init; } = true;
    
    /// <summary>是否为后台执行</summary>
    public bool IsBackground { get; init; }
    
    // ========== Gateway 相关 ==========
    
    /// <summary>并发通道 ID</summary>
    public string? ChannelId { get; init; }
    
    /// <summary>用户 ID</summary>
    public string? UserId { get; init; }
    
    // ========== 请求级参数 ==========
    
    /// <summary>
    /// 请求级 Model ID（用户在界面选择的模型）
    /// <para>
    /// 适用于 Native Agent 和 ACP Passthrough。
    /// 优先级：RequestModelId &gt; Agent.Model &gt; DefaultModel
    /// </para>
    /// </summary>
    public string? RequestModelId { get; init; }
    
    /// <summary>ACP 透传 session mode（如 build / ask）</summary>
    public string? AcpModeId { get; init; }
    
    // ========== 元数据 ==========
    
    /// <summary>附加元数据</summary>
    public Dictionary<string, object> Metadata { get; init; } = new();
    
    /// <summary>
    /// 创建子代理执行上下文
    /// </summary>
    public ChatExecutionContext CreateSubAgentContext(
        string subSessionId,
        AgentDefinition subAgent,
        string parentAgentName)
    {
        return new ChatExecutionContext
        {
            SessionId = subSessionId,
            Agent = subAgent,
            History = new List<ChatMessage>(),
            WorkingDirectory = WorkingDirectory,
            WorkspaceRoot = WorkspaceRoot,
            PermissionChannel = PermissionChannel,
            PermissionContext = PermissionContext,  // 权限上下文由 PermissionContext.CreateSubAgentContext 创建
            ParentSessionId = SessionId,
            ParentAgentName = parentAgentName,
            IsTopLevel = false,
            IsBackground = true,
            Metadata = new Dictionary<string, object>(Metadata)
        };
    }
}
