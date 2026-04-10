using Seeing.Agent.Core;
using Seeing.Agent.Core.Events;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;

namespace Seeing.Agent.Tui.Integration.Adapters;

/// <summary>
/// Agent 适配器接口 - 解耦 TUI 与 AgentExecutor
/// </summary>
public interface IAgentAdapter
{
    /// <summary>
    /// 执行 Agent 并返回事件流
    /// </summary>
    /// <param name="definition">Agent 定义</param>
    /// <param name="context">执行上下文</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>事件流</returns>
    IAsyncEnumerable<IMessageEvent> ExecuteStreamAsync(
        AgentDefinition definition,
        AgentContext context,
        CancellationToken cancellationToken);

    /// <summary>
    /// 获取所有可用的 Agent
    /// </summary>
    Task<IReadOnlyList<AgentInfo>> GetAgentsAsync();

    /// <summary>
    /// 获取默认 Agent 名称
    /// </summary>
    Task<string> GetDefaultAgentNameAsync();

    /// <summary>
    /// 获取有效的模型
    /// </summary>
    Task<ModelReference?> GetEffectiveModelAsync(string agentName);

    /// <summary>
    /// 切换 Agent
    /// </summary>
    Task SwitchAgentAsync(string agentName);

    /// <summary>
    /// 设置会话模型覆盖
    /// </summary>
    Task SetSessionModelOverrideAsync(string agentName, string modelId);

    /// <summary>
    /// 当前模型
    /// </summary>
    string? CurrentModel { get; }
}