namespace Seeing.Agent.WebUI.Models;

/// <summary>
/// 权限请求类型
/// </summary>
public enum PermissionRequestType
{
    /// <summary>工具执行权限</summary>
    Tool,

    /// <summary>子代理调用权限</summary>
    SubAgent,

    /// <summary>文件写入权限</summary>
    Write,

    /// <summary>文件读取权限</summary>
    Read,

    /// <summary>通用确认权限</summary>
    Confirmation
}

/// <summary>
/// 决策作用范围
/// </summary>
public enum DecisionScope
{
    /// <summary>仅本次，不记忆</summary>
    Once,

    /// <summary>会话内允许此文件</summary>
    SessionFile,

    /// <summary>会话内允许此目录</summary>
    SessionDirectory,

    /// <summary>会话内始终允许（工具/网络操作）</summary>
    Session
}

/// <summary>
/// 权限请求视图模型
/// </summary>
public class PermissionRequestViewModel
{
    /// <summary>请求 ID</summary>
    public string RequestId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>权限请求 ID（与 Core 层事件对应）</summary>
    public string PermissionId { get; set; } = "";

    /// <summary>请求类型</summary>
    public PermissionRequestType Type { get; set; }

    /// <summary>权限类型 (filesystem.read, filesystem.write, tool.execute, network.fetch, shell.execute)</summary>
    public string PermissionKind { get; set; } = "";

    /// <summary>目标名称（工具名/代理名/文件路径）</summary>
    public string Target { get; set; } = "";

    /// <summary>资源标识</summary>
    public string? Resource { get; set; }

    /// <summary>描述信息</summary>
    public string? Description { get; set; }

    /// <summary>参数或内容预览</summary>
    public object? Arguments { get; set; }

    /// <summary>风险等级 (low, medium, high, critical)</summary>
    public string RiskLevel { get; set; } = "medium";

    /// <summary>风险警告</summary>
    public string? RiskWarning { get; set; }

    /// <summary>是否有高风险</summary>
    public bool IsHighRisk { get; set; }

    /// <summary>提示消息</summary>
    public string? Message { get; set; }

    /// <summary>超时时间（秒）</summary>
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>请求时间</summary>
    public DateTime RequestTime { get; set; } = DateTime.Now;

    /// <summary>时间戳</summary>
    public DateTime Timestamp { get; set; } = DateTime.Now;

    /// <summary>会话 ID</summary>
    public string? SessionId { get; set; }

    /// <summary>是否为文件系统操作（支持目录级选项）</summary>
    public bool IsFileSystemOperation =>
        PermissionKind.StartsWith("filesystem.", StringComparison.OrdinalIgnoreCase);

    /// <summary>获取文件的父目录路径</summary>
    public string? GetDirectoryPath()
    {
        if (string.IsNullOrEmpty(Resource)) return null;
        var dir = System.IO.Path.GetDirectoryName(Resource);
        return string.IsNullOrEmpty(dir) ? null : dir + System.IO.Path.DirectorySeparatorChar;
    }

    public string GetRequestTypeText()
    {
        return Type switch
        {
            PermissionRequestType.Tool => "工具执行",
            PermissionRequestType.SubAgent => "子代理调用",
            PermissionRequestType.Write => "文件写入",
            PermissionRequestType.Read => "文件读取",
            PermissionRequestType.Confirmation => "操作确认",
            _ => "权限请求"
        };
    }

    public string GetRequestTypeIcon()
    {
        return Type switch
        {
            PermissionRequestType.Tool => "tool",
            PermissionRequestType.SubAgent => "robot",
            PermissionRequestType.Write => "file-text",
            PermissionRequestType.Read => "file-text",
            PermissionRequestType.Confirmation => "question-circle",
            _ => "info-circle"
        };
    }
}

/// <summary>
/// 权限决策结果
/// </summary>
public class PermissionDecisionViewModel
{
    /// <summary>是否批准</summary>
    public bool Approved { get; set; }

    /// <summary>决策作用范围</summary>
    public DecisionScope Scope { get; set; }

    /// <summary>决策时间</summary>
    public DateTime DecisionTime { get; set; } = DateTime.Now;
}
