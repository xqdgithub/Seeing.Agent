using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Permissions;

namespace Seeing.Agent.Abstractions.Tools;

/// <summary>
/// 工具管理器契约 - 工具的注册、查询与执行
/// </summary>
public interface IToolManager
{
    /// <summary>获取全部可用工具</summary>
    IReadOnlyCollection<ITool> GetTools();

    /// <summary>按 ID 获取工具</summary>
    ITool? GetTool(string id);

    /// <summary>注册工具（自动应用装饰器链）</summary>
    Task RegisterToolAsync(ITool tool, CancellationToken cancellationToken = default);

    /// <summary>注销工具</summary>
    bool UnregisterTool(string toolId);

    /// <summary>启用/禁用工具</summary>
    Task SetToolEnabledAsync(string toolId, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// 执行工具调用（权限检查由 AgentExecutor 统一处理）
    /// </summary>
    Task<ToolResult> ExecuteAsync(
        ToolCall toolCall,
        string sessionId = "",
        CancellationToken cancellationToken = default,
        Func<IMessageEvent, ValueTask>? emitAsync = null,
        IPermissionChannel? permissionChannel = null);
}
