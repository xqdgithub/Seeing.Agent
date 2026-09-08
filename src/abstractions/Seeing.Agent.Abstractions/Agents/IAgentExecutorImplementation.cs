using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Agent.Abstractions.Agents;

/// <summary>
/// 门面执行器的运行时实现口 — 每种 <see cref="AgentRuntime"/> 对应一个实现。
/// </summary>
public interface IAgentExecutorImplementation : IAgentExecutor
{
    /// <summary>此实现所支持的运行时类型</summary>
    AgentRuntime SupportedRuntime { get; }

    /// <inheritdoc/>
    IAsyncEnumerable<IMessageEvent> ExecuteAsync(
        AgentDefinition definition,
        IReadOnlyList<ChatMessage> messages,
        AgentContext context,
        CancellationToken cancellationToken = default);
}
