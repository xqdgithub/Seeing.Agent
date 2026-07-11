namespace Seeing.Agent.App.Models;

/// <summary>
/// 聊天选项 - 可选的执行配置
/// </summary>
public record ChatOptions
{
    /// <summary>指定 Agent ID（可选，使用默认）</summary>
    public string? AgentId { get; init; }
    
    /// <summary>指定 Model ID（可选）</summary>
    public string? ModelId { get; init; }
    
    /// <summary>ACP Mode ID（可选）</summary>
    public string? ModeId { get; init; }
    
    /// <summary>工作目录（可选，覆盖会话默认）</summary>
    public string? WorkingDirectory { get; init; }
    
    /// <summary>并发通道 ID（Gateway 用于队列隔离）</summary>
    public string? ChannelId { get; init; }
    
    /// <summary>用户 ID（Gateway 用于审计）</summary>
    public string? UserId { get; init; }
}
