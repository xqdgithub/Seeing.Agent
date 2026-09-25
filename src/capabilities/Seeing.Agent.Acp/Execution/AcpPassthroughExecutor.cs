using Seeing.Agent.Abstractions.Agents;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Acp.Mapping;
using Seeing.Agent.Acp.Configuration;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Agent.Acp.Execution;

/// <summary>
/// Passthrough 模式执行器，产出 <see cref="IMessageEvent"/> 流。
/// </summary>
public sealed class AcpPassthroughExecutor
{
    /// <summary>取消后排空缓冲事件的有界等待上限；超时即强制结束，避免后端不响应取消时挂死。</summary>
    private static readonly TimeSpan s_drainTimeout = TimeSpan.FromSeconds(2);

    private readonly IAcpSessionRunner _sessionRunner;
    private readonly ContentBlockMapper _contentMapper;
    private readonly AcpEventMapper _eventMapper;
    private readonly IOptionsMonitor<AcpOptions> _options;
    private readonly ILogger<AcpPassthroughExecutor> _logger;

    public AcpPassthroughExecutor(
        IAcpSessionRunner sessionRunner,
        ContentBlockMapper contentMapper,
        AcpEventMapper eventMapper,
        IOptionsMonitor<AcpOptions> options,
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
        IReadOnlyList<ChatMessage> messages,
        AgentContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!_options.CurrentValue.Enabled)
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
        var backendId = agent.AcpBackend ?? _options.CurrentValue.DefaultBackend
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
            UserInput = messages.LastOrDefault()?.Content
        };

        yield return new StreamStartEvent
        {
            SessionId = context.SessionId,
            LoopId = loopId,
            Step = 0
        };

        var sink = new EventYieldingSink(_eventMapper, context.SessionId, _logger, loopId);
        var prompt = _contentMapper.MapUserDelta(context, messages);
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
            ModelId = TryGetMetadataString(context, AgentContextKeys.RequestModelId),
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
        var cancelled = false;

        // 取消时 ReadAllAsync 的 OCE 若直接逃逸迭代器，会跳过下方 LoopCancelledEvent 终态。
        // 故在此捕获 OCE，再以有界超时排空取消前已产生的事件（对齐 Native AgentExecutor）。
        await using (var enumerator = sink.ReadAllAsync(cancellationToken).GetAsyncEnumerator(cancellationToken))
        {
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }

                if (!hasNext)
                    break;

                eventCount++;
                _logger.LogDebug(
                    "ACP passthrough forwarding event #{Count}: {EventType}",
                    eventCount,
                    enumerator.Current.Type);
                yield return enumerator.Current;
            }
        }

        if (cancelled)
        {
            // 取消路径主动关闭 channel（幂等）：排空自然收敛，后端即便忽略取消也不会卡住排空。
            sink.Complete();

            // 有界排空取消前已产生的缓冲事件，避免 UI 丢帧；超时后强制结束，
            // 确保无论如何都能到达下方 LoopCancelledEvent 终态。
            using var drainCts = new CancellationTokenSource(s_drainTimeout);
            await using var drainEnumerator = sink.ReadAllAsync(drainCts.Token).GetAsyncEnumerator(drainCts.Token);
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await drainEnumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning(
                        "ACP passthrough drain timed out session={SessionId} loop={LoopId}",
                        context.SessionId,
                        loopId);
                    break;
                }

                if (!hasNext)
                    break;

                eventCount++;
                yield return drainEnumerator.Current;
            }
        }

        _logger.LogInformation(
            "ACP passthrough forwarded {EventCount} events, awaiting run result session={SessionId} loop={LoopId}",
            eventCount,
            context.SessionId,
            loopId);

        AcpRunResult result;
        if (cancelled)
        {
            // 取消路径不再等待可能忽略取消的后端，避免执行器永久挂起；
            // 附加仅故障观察的续延，防止未观察任务异常。
            _ = runTask.ContinueWith(
                static (task, logger) =>
                {
                    if (task.Exception is { } ex)
                        ((ILogger)logger!).LogDebug(ex, "ACP cancelled run task faulted");
                },
                _logger,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            result = new AcpRunResult { Success = false, Error = "cancelled", Text = "" };
        }
        else
        {
            try
            {
                result = await runTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                result = new AcpRunResult { Success = false, Error = "cancelled", Text = "" };
            }
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
