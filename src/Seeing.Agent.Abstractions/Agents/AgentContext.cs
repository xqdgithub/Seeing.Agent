using Seeing.Agent.Abstractions.Permissions;

namespace Seeing.Agent.Abstractions.Agents;

/// <summary>
/// Agent 上下文 - 执行时的运行时信息
/// <para>
/// 支持多入口（TUI/API/CLI）和子代理调用。
/// 通过 IServiceProvider 获取运行时服务。
/// </para>
/// </summary>
public class AgentContext
{
    /// <summary>会话 ID</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>消息 ID</summary>
    public string MessageId { get; set; } = string.Empty;

    /// <summary>取消令牌</summary>
    public CancellationToken CancellationToken { get; set; }

    /// <summary>
    /// 服务提供者（用于获取 AgentExecutor、ToolManager 等）
    /// </summary>
    public IServiceProvider? Services { get; set; }

    /// <summary>
    /// 权限请求通道（多入口抽象）
    /// </summary>
    public IPermissionChannel? PermissionChannel { get; set; }

    /// <summary>权限上下文</summary>
    public PermissionContext? PermissionContext { get; set; }

    /// <summary>
    /// 工作目录
    /// </summary>
    public string WorkingDirectory { get; set; } = string.Empty;

    /// <summary>工作区根目录</summary>
    public string? WorkspaceRoot { get; set; }

    /// <summary>
    /// 父会话 ID（子代理调用时）
    /// </summary>
    public string? ParentSessionId { get; set; }

    /// <summary>
    /// 是否为后台任务
    /// </summary>
    public bool IsBackground { get; set; }

    /// <summary>元数据</summary>
    public Dictionary<string, object> Metadata { get; set; } = new();

    /// <summary>是否顶层 Agent（避免嵌套重复触发 Hook）</summary>
    public bool IsTopLevel { get; init; } = true;

    /// <summary>父代理名称（子代理时设置）</summary>
    public string? ParentAgentName { get; init; }
}