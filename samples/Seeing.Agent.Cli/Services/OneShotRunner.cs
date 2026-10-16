using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Session.Core;
using System.Threading.Channels;

namespace Seeing.Agent.Cli.Services;

/// <summary>
/// 一次性提示词执行编排：建会话 →（可选）设 yolo → 订阅事件 → 投递 → 收终态 → 输出结果。
/// <para>纯编排，依赖 <see cref="ISessionRuntime"/> / <see cref="ISessionDirectory"/> / <see cref="ISessionManager"/> 端口，便于单测。</para>
/// </summary>
public sealed class OneShotRunner
{
    private const int TitleMaxLength = 60;

    private readonly ISessionRuntime _runtime;
    private readonly ISessionDirectory _directory;
    private readonly ISessionManager _sessionManager;
    private readonly OneShotOutput _output;
    private readonly ILogger<OneShotRunner>? _logger;

    public OneShotRunner(
        ISessionRuntime runtime,
        ISessionDirectory directory,
        ISessionManager sessionManager,
        OneShotOutput output,
        ILogger<OneShotRunner>? logger = null)
    {
        _runtime = runtime;
        _directory = directory;
        _sessionManager = sessionManager;
        _output = output;
        _logger = logger;
    }

    /// <summary>执行一次并返回退出码（0 成功 / 1 失败 / 130 取消）。</summary>
    public async Task<int> RunAsync(OneShotOptions options, CancellationToken ct)
    {
        using var timeoutCts = options.TimeoutSeconds is { } seconds
            ? new CancellationTokenSource(TimeSpan.FromSeconds(seconds))
            : null;
        using var linked = timeoutCts is null
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var token = linked.Token;

        var session = await _directory.CreateAsync(BuildTitle(options.Prompt), options.Agent, ct: token)
            .ConfigureAwait(false);

        if (options.Yolo)
            await _sessionManager.SetAutoApproveAsync(session.Id, SessionAutoApprove.Enabled, token)
                .ConfigureAwait(false);

        // 事件泵：先订阅（自动回放环形缓冲）再投递，避免漏掉首批事件。
        var channel = Channel.CreateUnbounded<IMessageEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var pump = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in _runtime.SubscribeEvents(session.Id, pumpCts.Token).ConfigureAwait(false))
                    await channel.Writer.WriteAsync(evt, pumpCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常停止
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "事件订阅循环异常");
                channel.Writer.TryComplete(ex);
                return;
            }

            channel.Writer.TryComplete();
        });

        try
        {
            var delivered = await _runtime.DeliverAsync(new InboxMessage
            {
                TargetSessionId = session.Id,
                Sender = new InboxSender { Kind = InboxSenderKind.Human },
                Body = options.Prompt,
                Delivery = InboxDelivery.Wake,
                Presentation = InboxPresentation.User,
                AgentId = options.Agent,
                ModelId = options.Model,
            }, token).ConfigureAwait(false);

            if (!delivered.Success)
            {
                _output.WriteDiagnostic($"[error] 投递失败: {delivered.Error}");
                return 1;
            }

            var status = ExecutionStatus.Completed;
            var gotTerminal = false;
            await foreach (var evt in channel.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                _output.Apply(evt);
                if (evt is ExecutionCompleteEvent complete)
                {
                    status = complete.Status;
                    gotTerminal = true;
                    break;
                }
            }

            await _runtime.WaitForIdleAsync(session.Id, token).ConfigureAwait(false);
            _output.WriteFinal();

            if (!gotTerminal)
            {
                _output.WriteDiagnostic("[error] 未收到执行终态事件");
                return 1;
            }

            if (_output.HasError || status is ExecutionStatus.Failed or ExecutionStatus.Cancelled)
                return 1;

            return 0;
        }
        catch (OperationCanceledException)
        {
            // 超时或 Ctrl+C：取消在途执行。
            _output.WriteDiagnostic(timeoutCts?.IsCancellationRequested == true
                ? $"[error] 执行超时（{options.TimeoutSeconds}s）"
                : "[error] 已取消");
            await TryStopAsync(session.Id).ConfigureAwait(false);
            return timeoutCts?.IsCancellationRequested == true ? 1 : 130;
        }
        finally
        {
            await pumpCts.CancelAsync().ConfigureAwait(false);
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task TryStopAsync(string sessionId)
    {
        try
        {
            await _runtime.StopAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "取消会话执行失败: {SessionId}", sessionId);
        }
    }

    private static string BuildTitle(string prompt)
    {
        var normalized = prompt.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (normalized.Length == 0)
            return "一次性任务";
        if (normalized.Length <= TitleMaxLength)
            return normalized;

        return normalized[..TitleMaxLength];
    }
}
