using Seeing.Agent.Abstractions.Agents;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Agent.Acp.Execution;

/// <summary>
/// ACP 执行器 - 装饰 <see cref="IAgentExecutor"/>，按 Agent Runtime 分发执行。
/// </summary>
public sealed class AcpAgentExecutor : IAgentExecutor
{
    private readonly IAgentExecutor _inner;
    private readonly AcpPassthroughExecutor _passthroughExecutor;
    private readonly IOptions<SeeingAgentOptions> _options;

    public AcpAgentExecutor(
        NativeAgentExecutor inner,
        AcpPassthroughExecutor passthroughExecutor,
        IOptions<SeeingAgentOptions> options)
    {
        _inner = inner;
        _passthroughExecutor = passthroughExecutor;
        _options = options;
    }

    public async IAsyncEnumerable<IMessageEvent> ExecuteAsync(
        AgentDefinition agent,
        IReadOnlyList<ChatMessage> messages,
        AgentContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (agent.Runtime == AgentRuntime.AcpPassthrough)
        {
            if (!_options.Value.Acp.Enabled)
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

            yield break;
        }

        await foreach (var evt in _inner.ExecuteAsync(agent, messages, context, cancellationToken).ConfigureAwait(false))
            yield return evt;
    }
}