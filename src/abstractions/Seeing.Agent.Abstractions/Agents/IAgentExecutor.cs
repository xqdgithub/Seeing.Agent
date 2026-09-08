using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Llm;
namespace Seeing.Agent.Abstractions.Agents
{
    /// <summary>
    /// 统一 Agent 执行入口（定义 + 消息入参 → 流式事件）
    /// </summary>
    public interface IAgentExecutor
    {
        /// <summary>
        /// 执行 Agent
        /// </summary>
        /// <param name="definition">Agent 定义（纯数据）</param>
        /// <param name="messages">输入消息流（显式入参，不依赖 Context.History）</param>
        /// <param name="context">执行上下文（环境快照）</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>流式消息事件序列</returns>
        IAsyncEnumerable<IMessageEvent> ExecuteAsync(
            AgentDefinition definition,
            IReadOnlyList<ChatMessage> messages,
            AgentContext context,
            CancellationToken cancellationToken = default);
    }
}