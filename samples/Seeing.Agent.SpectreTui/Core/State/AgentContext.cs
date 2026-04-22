namespace Seeing.Agent.SpectreTui.Core.State;

/// <summary>
/// Agent 运行时上下文 - 管理 Agent、模型、会话和资源配置
/// </summary>
public class AgentContext
{
    // ========== 工作区 ==========
    
    /// <summary>工作区根目录</summary>
    public string WorkspaceRoot { get; set; } = "";
    
    /// <summary>当前Agent键</summary>
    public string CurrentAgentKey { get; set; } = "primary";
    
    /// <summary>会话ID</summary>
    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    
    // ========== 配置信息 ==========
    
    /// <summary>当前模型</summary>
    public string? CurrentModel { get; set; }
    
    // ========== 工具状态（只读统计） ==========
    
    /// <summary>工具数量</summary>
    public int ToolCount { get; set; }
    
    /// <summary>技能数量</summary>
    public int SkillCount { get; set; }
    
    /// <summary>MCP服务器数量</summary>
    public int McpServerCount { get; set; }
    
    /// <summary>扩展数量</summary>
    public int ExtensionCount { get; set; }
    
    /// <summary>消息数量</summary>
    public int MessageCount { get; set; }
}