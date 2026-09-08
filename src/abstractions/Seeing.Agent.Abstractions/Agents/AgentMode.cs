namespace Seeing.Agent.Abstractions.Agents;

/// <summary>
/// Agent 模式 - 定义 Agent 的角色
/// </summary>
public enum AgentMode
{
    /// <summary>
    /// 主 Agent - 用户直接交互的代理
    /// <para>仅出现在 UI 的代理选择列表中</para>
    /// </summary>
    Primary,

    /// <summary>
    /// 子 Agent - 只能被其他 Agent 调用
    /// <para>不出现在 UI 的代理选择列表中</para>
    /// </summary>
    SubAgent,

    /// <summary>
    /// 通用 Agent - 可作为主 Agent 或子 Agent
    /// <para>出现在 UI 列表中，也可被子任务调用</para>
    /// <para>在 GetPrimaryAgentsAsync 中会被包含</para>
    /// </summary>
    All
}