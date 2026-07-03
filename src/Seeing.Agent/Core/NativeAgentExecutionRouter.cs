using Seeing.Agent.Core.Events;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;

namespace Seeing.Agent.Core
{
    /// <summary>
    /// 原生执行路由 - 将执行委托给 <see cref="AgentExecutor"/>
    /// </summary>
    public class NativeAgentExecutionRouter : IAgentExecutionRouter
    {
        private readonly AgentExecutor _executor;

        public NativeAgentExecutionRouter(AgentExecutor executor)
        {
            _executor = executor;
        }

        /// <inheritdoc/>
        public IAsyncEnumerable<IMessageEvent> ExecuteAsync(
            AgentDefinition agent,
            AgentContext context,
            CancellationToken cancellationToken = default)
        {
            return _executor.ExecuteAsync(agent, context, cancellationToken);
        }
    }
}
