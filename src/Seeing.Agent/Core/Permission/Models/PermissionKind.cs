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
    /// <summary>允许执行</summary>
    Allow = 0,
    
    /// <summary>拒绝执行</summary>
    Deny = 1,
    
    /// <summary>需要用户确认</summary>
    Ask = 2
}

/// <summary>
/// 条件组合逻辑
/// </summary>
public enum ConditionLogic
{
    /// <summary>所有条件都必须满足</summary>
    And = 0,
    
    /// <summary>任一条件满足即可</summary>
    Or = 1
}

/// <summary>
/// 文件操作类型
/// </summary>
public enum FileOperation
{
    /// <summary>读取文件</summary>
    Read = 0,
    
    /// <summary>写入文件</summary>
    Write = 1,
    
    /// <summary>删除文件</summary>
    Delete = 2,
    
    /// <summary>执行文件</summary>
    Execute = 3,
    
    /// <summary>列出目录内容</summary>
    List = 4
}
