using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Acp.Mapping;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Events;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Llm;

namespace Seeing.Agent.Acp.Execution;

/// <summary>
/// Passthrough 模式执行器，产出 <see cref="IMessageEvent"/> 流。
/// </summary>
public sealed class AcpPassthroughExecutor
{
    private readonly IAcpSessionRunner _sessionRunner;
    private readonly ContentBlockMapper _contentMapper;
    private readonly AcpEventMapper _eventMapper;
    private readonly IOptions<SeeingAgentOptions> _options;
    private readonly ILogger<AcpPassthroughExecutor> _logger;

    public AcpPassthroughExecutor(
        IAcpSessionRunner sessionRunner,
        ContentBlockMapper contentMapper,
        AcpEventMapper eventMapper,
        IOptions<SeeingAgentOptions> options,
        ILogger<AcpPassthroughExecutor> logger)
    {
        _sessionRunner = sessionRunner;
        _contentMapper = contentMapper;
        _eventMapper = eventMapper;
        _options = options;
        _logger = logger;
    }

    public async IAsyncEnumerable<IMessageEvent> ExecuteAsync(
        AgentDefinition agent,
        AgentContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!_options.Value.Acp.Enabled)
        {
            yield return new ErrorEvent
            {
                SessionId = context.SessionId,
                Message = "ACP integration is disabled in configuration.",
                Source = "acp"
            };
            yield break;
        }

        var loopId = Guid.NewGuid().ToString("N");
        var loopStart = DateTime.Now;
        var backendId = agent.AcpBackend ?? _options.Value.Acp.DefaultBackend
            ?? throw new InvalidOperationException("ACP backend is not configured for passthrough agent.");

        _logger.LogInformation(
            "ACP passthrough loop start session={SessionId} loop={LoopId} backend={BackendId} agent={AgentName}",
            context.SessionId,
            loopId,
            backendId,
            agent.Name);

        yield return new LoopStartEvent
        {
            SessionId = context.SessionId,
            LoopId = loopId,
            UserInput = context.History.LastOrDefault()?.Content
        };

        yield return new StreamStartEvent
        {
            SessionId = context.SessionId,
            LoopId = loopId,
            Step = 0
        };

        var sink = new EventYieldingSink(_eventMapper, context.SessionId, _logger, loopId);
        var prompt = _contentMapper.MapUserDelta(context);
        var workingDirectory = string.IsNullOrWhiteSpace(context.WorkingDirectory)
            ? Environment.CurrentDirectory
            : context.WorkingDirectory;

        var runRequest = new AcpRunRequest
        {
            Scope = "passthrough",
            ScopeKey = context.SessionId,
            BackendId = backendId,
            SeeingSessionId = context.SessionId,
            LoopId = loopId,
            Prompt = prompt,
            WorkingDirectory = workingDirectory,
            ModeId = TryGetMetadataString(context, AgentContextKeys.AcpModeId),
            ModelId = TryGetMetadataString(context, AgentContextKeys.AcpModelId),
            ParentContext = context
        };

        var runTask = _sessionRunner.RunAsync(runRequest, sink, cancellationToken);

        // RunAsync 完成后必须关闭 channel，否则 ReadAllAsync 会永久阻塞，LoopComplete 无法发出。
        _ = runTask.ContinueWith(
            static (_, state) =>
            {
                var (eventSink, logger) = ((EventYieldingSink, ILogger))state!;
                eventSink.Complete();
                logger.LogDebug("ACP passthrough run task finished, closing event channel");
            },
            (sink, (ILogger)_logger),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        var eventCount = 0;
        await foreach (var evt in sink.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            eventCount++;
            _logger.LogDebug(
                "ACP passthrough forwarding event #{Count}: {EventType}",
                eventCount,
                evt.Type);
            yield return evt;
        }

        _logger.LogInformation(
            "ACP passthrough forwarded {EventCount} events, awaiting run result session={SessionId} loop={LoopId}",
            eventCount,
            context.SessionId,
            loopId);

        AcpRunResult result;
        var cancelled = false;
        try
        {
            result = await runTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            result = new AcpRunResult { Success = false, Error = "cancelled", Text = "" };
        }

        if (cancelled)
        {
            _logger.LogWarning(
                "ACP passthrough cancelled session={SessionId} loop={LoopId}",
                context.SessionId,
                loopId);

            yield return new LoopCancelledEvent
            {
                SessionId = context.SessionId,
                LoopId = loopId,
                Reason = "user"
            };
            yield break;
        }

        if (!result.Success)
        {
            _logger.LogError(
                "ACP passthrough failed session={SessionId} loop={LoopId} error={Error}",
                context.SessionId,
                loopId,
                result.Error);

            yield return new ErrorEvent
            {
                SessionId = context.SessionId,
                LoopId = loopId,
                Message = result.Error ?? "ACP execution failed",
                Source = "acp"
            };

            yield return new LoopCompleteEvent
            {
                SessionId = context.SessionId,
                LoopId = loopId,
                Success = false,
                Error = result.Error,
                Duration = DateTime.Now - loopStart
            };
            yield break;
        }

        var finalText = result.Text?.Trim() ?? string.Empty;

        _logger.LogInformation(
            "ACP passthrough prompt complete session={SessionId} loop={LoopId} stopReason={StopReason} textLength={TextLength}",
            context.SessionId,
            loopId,
            result.StopReason ?? "(none)",
            finalText.Length);

        var assistantMessage = new ChatMessage
        {
            Role = "assistant",
            Content = finalText
        };

        yield return new StreamCompleteEvent
        {
            SessionId = context.SessionId,
            LoopId = loopId,
            Message = assistantMessage,
            Usage = result.Usage
        };

        yield return new LoopCompleteEvent
        {
            SessionId = context.SessionId,
            LoopId = loopId,
            Success = true,
            Duration = DateTime.Now - loopStart,
            Usage = result.Usage
        };

        _logger.LogInformation(
            "ACP passthrough loop complete session={SessionId} loop={LoopId} durationMs={DurationMs}",
            context.SessionId,
            loopId,
            (DateTime.Now - loopStart).TotalMilliseconds);
    }

    private static string? TryGetMetadataString(AgentContext context, string key)
    {
        if (!context.Metadata.TryGetValue(key, out var value))
            return null;

        return value switch
        {
            null => null,
            string s when string.IsNullOrWhiteSpace(s) => null,
            string s => s,
            _ => value.ToString()
        };
    }
}
