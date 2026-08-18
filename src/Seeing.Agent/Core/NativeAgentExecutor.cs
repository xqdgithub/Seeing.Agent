using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Agent.Core
{
    /// <summary>
    /// 原生执行器 - 将执行委托给 <see cref="AgentExecutor"/>
    /// </summary>
    public class NativeAgentExecutor : IAgentExecutor
    {
        private readonly AgentExecutor _executor;

        public NativeAgentExecutor(AgentExecutor executor)
        {
            _executor = executor;
        }

        /// <inheritdoc/>
        public IAsyncEnumerable<IMessageEvent> ExecuteAsync(
            AgentDefinition agent,
            IReadOnlyList<ChatMessage> messages,
            AgentContext context,
            CancellationToken cancellationToken = default)
        {
            return _executor.ExecuteAsync(agent, messages, context, cancellationToken);
        }
    }
}