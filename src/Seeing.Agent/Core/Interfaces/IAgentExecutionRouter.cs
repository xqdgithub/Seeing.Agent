using Seeing.Agent.Core.Events;
using Seeing.Agent.Core.Models;

namespace Seeing.Agent.Core.Interfaces
{
    /// <summary>
    /// Agent 执行路由 - 根据运行时类型将执行委托给对应引擎
    /// </summary>
    public interface IAgentExecutionRouter
    {
        /// <summary>
        /// 执行 Agent 并返回事件流
        /// </summary>
        IAsyncEnumerable<IMessageEvent> ExecuteAsync(
            AgentDefinition agent,
            AgentContext context,
            CancellationToken cancellationToken = default);
    }
}
