using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Background;

namespace Seeing.Agent.Core.Background;

/// <summary>
/// 完成通知投递循环（spec §10.1）：周期性调用 <see cref="IBackgroundActivityOutbox.PumpOnceAsync"/>，
/// 两次泵之间等待一个合并窗口让同一会话的完成通知先攒起来。
/// <para>
/// <b>为什么不用 <c>Task.Delay</c></b>：合并窗口必须由注入的 <see cref="TimeProvider"/> 驱动，
/// 测试才能用 <c>FakeTimeProvider</c> 确定性地推进，而不是真等 300ms。
/// </para>
/// <para>
/// 泵取异常只记日志并等下一个窗口：单次投递失败不能终止整个循环（批次已在盘上，不会丢）。
/// </para>
/// </summary>
internal sealed class BackgroundActivityNotificationDispatcher : IHostedService, IAsyncDisposable
{
    private readonly IBackgroundActivityOutbox _outbox;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _mergeWindow;
    private readonly ILogger<BackgroundActivityNotificationDispatcher> _logger;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public BackgroundActivityNotificationDispatcher(
        IBackgroundActivityOutbox outbox,
        TimeProvider timeProvider,
        ILogger<BackgroundActivityNotificationDispatcher> logger,
        TimeSpan? mergeWindow = null)
    {
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _mergeWindow = mergeWindow is { } window && window > TimeSpan.Zero
            ? window
            : BackgroundActivityOutbox.DefaultMergeWindow;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_loop is not null)
            return Task.CompletedTask;

        _cts = new CancellationTokenSource();
        _loop = RunAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var cts = _cts;
        var loop = _loop;

        _cts = null;
        _loop = null;

        if (cts is null)
            return;

        try
        {
            await cts.CancelAsync().ConfigureAwait(false);

            if (loop is not null)
                await loop.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 宿主关停超时：循环已收到取消信号，残留批次下次启动再处理（spec §10.4）。
        }
        finally
        {
            cts.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _outbox.PumpOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "后台活动完成通知泵取失败，等待下一个合并窗口");
            }

            try
            {
                await Task.Delay(_mergeWindow, _timeProvider, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
