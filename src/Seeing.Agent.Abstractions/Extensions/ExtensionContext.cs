using Microsoft.Extensions.Configuration;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Abstractions.Mcp;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Tools;

namespace Seeing.Agent.Abstractions.Extensions;

/// <summary>
/// 扩展上下文 - 提供运行时信息和服务引用
/// </summary>
public class ExtensionContext
{
    /// <summary>服务提供者</summary>
    public IServiceProvider Services { get; set; } = null!;

    /// <summary>配置</summary>
    public IConfiguration Configuration { get; set; } = null!;

    /// <summary>当前工作目录</summary>
    public string Directory { get; set; } = "";

    /// <summary>工作区根目录</summary>
    public string WorkspaceRoot { get; set; } = "";

    // 核心服务引用（SkillManager / CommandRegistry 由 Task 12 恢复注入）

    /// <summary>Hook 管理器</summary>
    public IHookManager HookManager { get; set; } = null!;

    /// <summary>工具管理器</summary>
    public IToolManager ToolManager { get; set; } = null!;

    /// <summary>权限服务</summary>
    public IPermissionService PermissionService { get; set; } = null!;

    /// <summary>Agent 注册表</summary>
    public IAgentRegistry AgentRegistry { get; set; } = null!;

    /// <summary>MCP 客户端管理器</summary>
    public IMcpManager McpClientManager { get; set; } = null!;
}