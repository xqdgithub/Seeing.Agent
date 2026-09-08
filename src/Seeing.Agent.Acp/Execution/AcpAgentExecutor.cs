using Seeing.Agent.Abstractions.Agents;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using Seeing.Agent.Acp.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Agent.Acp.Execution;

/// <summary>
/// ACP Passthrough 执行器实现 — 由 <see cref="IAgentExecutor"/> 门面按 Runtime 分发。
/// </summary>
public sealed class AcpAgentExecutor : IAgentExecutorImplementation
{
    private readonly AcpPassthroughExecutor _passthroughExecutor;
    private readonly IOptionsMonitor<AcpOptions> _options;

    public AcpAgentExecutor(
        AcpPassthroughExecutor passthroughExecutor,
        IOptionsMonitor<AcpOptions> options)
    {
        _passthroughExecutor = passthroughExecutor;
        _options = options;
    }

    /// <inheritdoc/>
    public AgentRuntime SupportedRuntime => AgentRuntime.AcpPassthrough;

    public async IAsyncEnumerable<IMessageEvent> ExecuteAsync(
        AgentDefinition agent,
        IReadOnlyList<ChatMessage> messages,
        AgentContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!_options.CurrentValue.Enabled)
        {
            yield return new ErrorEvent
            {
                SessionId = context.SessionId,
                Message = "ACP passthrough requested but ACP is disabled.",
                Source = "acp"
            };
            yield break;
        }

        await foreach (var evt in _passthroughExecutor.ExecuteAsync(agent, messages, context, cancellationToken)
                           .ConfigureAwait(false))
        {
            yield return evt;
        }
    }
}
