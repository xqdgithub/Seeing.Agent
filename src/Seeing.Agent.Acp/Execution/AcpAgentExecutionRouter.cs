using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Events;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;

namespace Seeing.Agent.Acp.Execution;

/// <summary>
/// 装饰 <see cref="IAgentExecutionRouter"/>，按 Agent Runtime 分发执行。
/// </summary>
public sealed class AcpAgentExecutionRouter : IAgentExecutionRouter
{
    private readonly IAgentExecutionRouter _inner;
    private readonly AcpPassthroughExecutor _passthroughExecutor;
    private readonly IOptions<SeeingAgentOptions> _options;

    public AcpAgentExecutionRouter(
        NativeAgentExecutionRouter inner,
        AcpPassthroughExecutor passthroughExecutor,
        IOptions<SeeingAgentOptions> options)
    {
        _inner = inner;
        _passthroughExecutor = passthroughExecutor;
        _options = options;
    }

    public async IAsyncEnumerable<IMessageEvent> ExecuteAsync(
        AgentDefinition agent,
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

            await foreach (var evt in _passthroughExecutor.ExecuteAsync(agent, context, cancellationToken)
                               .ConfigureAwait(false))
            {
                yield return evt;
            }

            yield break;
        }

        await foreach (var evt in _inner.ExecuteAsync(agent, context, cancellationToken).ConfigureAwait(false))
            yield return evt;
    }
}
