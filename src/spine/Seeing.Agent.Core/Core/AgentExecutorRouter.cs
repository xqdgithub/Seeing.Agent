using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Agent.Core;

/// <summary>
/// 按 <see cref="AgentDefinition.Runtime"/> 将执行分发给已注册的
/// <see cref="IAgentExecutorImplementation"/>。
/// </summary>
public sealed class AgentExecutorRouter : IAgentExecutor
{
    private readonly IReadOnlyDictionary<AgentRuntime, IAgentExecutorImplementation> _byRuntime;

    public AgentExecutorRouter(IEnumerable<IAgentExecutorImplementation> implementations)
    {
        ArgumentNullException.ThrowIfNull(implementations);
        _byRuntime = implementations.ToDictionary(i => i.SupportedRuntime);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<IMessageEvent> ExecuteAsync(
        AgentDefinition definition,
        IReadOnlyList<ChatMessage> messages,
        AgentContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (!_byRuntime.TryGetValue(definition.Runtime, out var executor))
        {
            throw new InvalidOperationException(
                $"No IAgentExecutorImplementation registered for runtime '{definition.Runtime}'.");
        }

        return executor.ExecuteAsync(definition, messages, context, cancellationToken);
    }
}
