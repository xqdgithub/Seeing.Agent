using System.Threading.Channels;
using Seeing.Gateway.WeCom.Connection;

namespace Seeing.Gateway.WeCom;

/// <summary>
/// 单条入站消息的企微流式回复。
/// <para>
/// 协议约束：每条用户消息仅一条 <c>stream_id</c>，且仅 <see cref="CompleteAsync"/> /
/// <see cref="FailAsync"/> 可发送 <c>finish=true</c>。处理中占位与正文共用同一 stream。
/// </para>
/// </summary>
public sealed class WeComStreamState : IAsyncDisposable, IWeComActiveStreamHandle
{
    private const string ProcessingText = "🤔 Thinking...";
    private static readonly TimeSpan ConnectionWaitTimeout = TimeSpan.FromSeconds(60);

    private readonly IWeComStreamSender _sender;
    private readonly WeComAibotWsClient? _client;
    private readonly WeComWsFrame _requestFrame;
    private readonly WeComOptions _options;
    private readonly WeComActiveStreamRegistry? _registry;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private string? _streamId;
    private WeComWsFrame _replyFrame;
    private string _visibleText = ProcessingText;
    private DateTime _lastDeltaSentUtc = DateTime.MinValue;
    private bool _hasContent;
    private bool _completed;
    private bool _connectionDegraded;
    private bool _pendingOutbound;
    private bool _pendingFinish;
    private int _reconnectWaits;
    private CancellationTokenSource? _keepaliveCts;
    private Task? _keepaliveTask;
    private CancellationTokenSource? _publishCts;
    private Task? _publishLoopTask;
    private Channel<bool>? _publishChannel;

    public WeComStreamState(
        WeComAibotWsClient client,
        WeComWsFrame requestFrame,
        WeComOptions options,
        WeComActiveStreamRegistry? registry = null)
        : this(new WeComAibotStreamSender(client), client, requestFrame, options, registry)
    {
    }

    internal WeComStreamState(
        IWeComStreamSender sender,
        WeComAibotWsClient? client,
        WeComWsFrame requestFrame,
        WeComOptions options,
        WeComActiveStreamRegistry? registry = null)
    {
        _sender = sender;
        _client = client;
        _requestFrame = requestFrame;
        _replyFrame = requestFrame;
        _options = options;
        _registry = registry;
    }

    public int ReconnectWaits => _reconnectWaits;

    public bool IsStreamOpen => !_completed && !string.IsNullOrEmpty(_streamId);

    /// <summary>直接发送最终回复，不显示 Thinking 占位（用于斜杠命令等即时反馈）。</summary>
    public async Task SendInstantAsync(string text, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_completed, this);

        _streamId = WeComAibotWsClient.GenerateStreamId();
        _visibleText = FormatFinalText(text);
        await SendAsync(finish: true, cancellationToken, requireDelivery: true).ConfigureAwait(false);
        _completed = true;
        UnregisterActiveStream();
    }

    /// <summary>开启回复流并显示处理中占位（finish=false）。</summary>
    public async Task BeginAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_completed, this);

        _streamId = WeComAibotWsClient.GenerateStreamId();
        _visibleText = ProcessingText;
        RegisterActiveStream();
        await SendAsync(finish: false, cancellationToken).ConfigureAwait(false);

        _keepaliveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _keepaliveTask = RunProcessingKeepaliveAsync(_keepaliveCts.Token);

        _publishChannel = Channel.CreateUnbounded<bool>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        _publishCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _publishLoopTask = RunPublishLoopAsync(_publishCts.Token);
    }

    /// <summary>响应企微流式刷新回调，使用刷新帧的 req_id 回传当前内容。</summary>
    public async Task HandleRefreshAsync(WeComWsFrame refreshFrame, CancellationToken cancellationToken)
    {
        if (_completed || string.IsNullOrEmpty(_streamId))
            return;

        _replyFrame = refreshFrame;
        await SendAsync(finish: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 非阻塞调度正文增量：仅更新内存中的可见文本并由后台循环合并发送。
    /// Gateway 事件循环不应 await 此方法。
    /// </summary>
    public void SchedulePublish(string text)
    {
        if (!_options.StreamingEnabled || string.IsNullOrEmpty(text) || _completed)
            return;

        _hasContent = true;
        _visibleText = text;
        _ = StopKeepaliveAsync();
        _publishChannel?.Writer.TryWrite(true);
    }

    /// <summary>同步推送正文增量（finish=false），供测试与需要立即尝试发送的调用方使用。</summary>
    public async Task PublishAsync(string text, CancellationToken cancellationToken)
    {
        if (!_options.StreamingEnabled || string.IsNullOrEmpty(text))
            return;

        ObjectDisposedException.ThrowIf(_completed, this);

        _hasContent = true;
        _visibleText = text;
        await StopKeepaliveAsync().ConfigureAwait(false);

        var throttleMs = _options.EffectiveDeltaThrottleMilliseconds;
        var now = DateTime.UtcNow;
        if ((now - _lastDeltaSentUtc).TotalMilliseconds < throttleMs)
            return;

        await SendAsync(finish: false, cancellationToken).ConfigureAwait(false);
        _lastDeltaSentUtc = now;
    }

    /// <summary>在流式回复末尾追加权限确认提示（finish=false），不覆盖已有正文。</summary>
    public async Task PublishPermissionNoticeAsync(string notice, CancellationToken cancellationToken)
    {
        if (!IsStreamOpen || string.IsNullOrWhiteSpace(notice))
            return;

        var trimmed = notice.Trim();
        if (_visibleText.Contains(trimmed, StringComparison.Ordinal))
            return;

        _hasContent = true;
        _visibleText = string.Equals(_visibleText, ProcessingText, StringComparison.Ordinal)
            ? trimmed
            : $"{_visibleText.TrimEnd()}\n\n{trimmed}";
        await StopKeepaliveAsync().ConfigureAwait(false);
        await SendAsync(finish: false, cancellationToken).ConfigureAwait(false);
        _lastDeltaSentUtc = DateTime.UtcNow;
    }

    /// <summary>发送最终正文并结束流（finish=true）。</summary>
    public async Task CompleteAsync(string? finalText, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_completed, this);

        await StopPublishLoopAsync().ConfigureAwait(false);
        await StopKeepaliveAsync().ConfigureAwait(false);

        _visibleText = FormatFinalText(finalText);
        await SendAsync(finish: true, cancellationToken, requireDelivery: true).ConfigureAwait(false);
        _completed = true;
        UnregisterActiveStream();
    }

    /// <summary>发送错误并结束流（finish=true）。</summary>
    public async Task FailAsync(string error, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_completed, this);

        await StopPublishLoopAsync().ConfigureAwait(false);
        await StopKeepaliveAsync().ConfigureAwait(false);
        _visibleText = $"❌ {error}";
        await SendAsync(finish: true, cancellationToken, requireDelivery: true).ConfigureAwait(false);
        _completed = true;
        UnregisterActiveStream();
    }

    public async Task NotifyConnectionDegradedAsync(CancellationToken cancellationToken)
    {
        _connectionDegraded = true;
        await StopKeepaliveAsync().ConfigureAwait(false);
    }

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (_completed || string.IsNullOrEmpty(_streamId))
            return;

        _connectionDegraded = false;

        if (_pendingOutbound)
        {
            var finish = _pendingFinish;
            await TrySendOnceAsync(finish, cancellationToken).ConfigureAwait(false);
        }
        else if (_hasContent)
        {
            _publishChannel?.Writer.TryWrite(true);
        }

        if (!_hasContent && !_completed && _keepaliveCts == null)
        {
            _keepaliveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _keepaliveTask = RunProcessingKeepaliveAsync(_keepaliveCts.Token);
        }
    }

    public Task AbortAsync(string reason, CancellationToken cancellationToken) =>
        _completed
            ? Task.CompletedTask
            : FailAsync(reason, cancellationToken);

    private string FormatFinalText(string? finalText)
    {
        var text = string.IsNullOrWhiteSpace(finalText)
            ? (_hasContent ? _visibleText : "✅")
            : finalText;

        if (!string.IsNullOrWhiteSpace(_options.BotPrefix))
            text = $"{_options.BotPrefix}  {text}";

        return text;
    }

    private async Task RunPublishLoopAsync(CancellationToken cancellationToken)
    {
        if (_publishChannel == null)
            return;

        var throttle = TimeSpan.FromMilliseconds(_options.EffectiveDeltaThrottleMilliseconds);

        try
        {
            while (await _publishChannel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                DrainPublishSignals();

                if (_completed || !_hasContent)
                    continue;

                try
                {
                    await Task.Delay(throttle, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                DrainPublishSignals();

                if (_completed || !_hasContent)
                    continue;

                var now = DateTime.UtcNow;
                if ((now - _lastDeltaSentUtc).TotalMilliseconds < throttle.TotalMilliseconds)
                    continue;

                var sent = await TrySendOnceAsync(finish: false, cancellationToken).ConfigureAwait(false);
                if (sent)
                    _lastDeltaSentUtc = now;
                else if (_pendingOutbound && !_completed)
                    _publishChannel.Writer.TryWrite(true);
            }
        }
        catch (OperationCanceledException)
        {
            // stream completed or bridge cancelled
        }
    }

    private void DrainPublishSignals()
    {
        if (_publishChannel == null)
            return;

        while (_publishChannel.Reader.TryRead(out _))
        {
        }
    }

    private async Task StopPublishLoopAsync()
    {
        if (_publishCts == null)
            return;

        _publishCts.Cancel();
        _publishChannel?.Writer.TryComplete();

        if (_publishLoopTask != null)
        {
            try
            {
                await _publishLoopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected
            }
        }

        _publishCts.Dispose();
        _publishCts = null;
        _publishLoopTask = null;
        _publishChannel = null;
    }

    private async Task SendAsync(bool finish, CancellationToken cancellationToken, bool requireDelivery = false)
    {
        if (string.IsNullOrEmpty(_streamId))
            throw new InvalidOperationException("WeCom reply stream has not been started.");

        if (requireDelivery)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ConnectionWaitTimeout);

            while (true)
            {
                timeoutCts.Token.ThrowIfCancellationRequested();

                if (await TrySendOnceAsync(finish, timeoutCts.Token).ConfigureAwait(false))
                    return;

                _reconnectWaits++;
                await Task.Delay(TimeSpan.FromMilliseconds(500), timeoutCts.Token).ConfigureAwait(false);
            }
        }

        if (!CanSendNow())
        {
            _pendingOutbound = true;
            if (finish)
                _pendingFinish = true;
            return;
        }

        await TrySendOnceAsync(finish, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TrySendOnceAsync(bool finish, CancellationToken cancellationToken)
    {
        if (!CanSendNow())
        {
            _pendingOutbound = true;
            if (finish)
                _pendingFinish = true;
            return false;
        }

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!CanSendNow())
            {
                _pendingOutbound = true;
                if (finish)
                    _pendingFinish = true;
                return false;
            }

            await _sender.ReplyStreamAsync(
                _replyFrame,
                _streamId!,
                _visibleText,
                finish,
                ResolveLiveEpoch(),
                cancellationToken,
                ResolvePriority(finish)).ConfigureAwait(false);

            _pendingOutbound = false;
            _pendingFinish = false;
            return true;
        }
        catch (WeComConnectionEpochException)
        {
            _pendingOutbound = true;
            if (finish)
                _pendingFinish = true;
            return false;
        }
        catch (InvalidOperationException)
        {
            _pendingOutbound = true;
            if (finish)
                _pendingFinish = true;
            return false;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private bool CanSendNow()
    {
        if (_client == null)
            return true;

        if (_connectionDegraded)
            return false;

        return _client.IsConnected && _client.Connection.Outbound.IsReady;
    }

    private long ResolveLiveEpoch() => _client?.ConnectionEpoch ?? 0;

    private static WeComOutboundPriority ResolvePriority(bool finish) =>
        finish ? WeComOutboundPriority.Finish : WeComOutboundPriority.ContentDelta;

    private async Task RunProcessingKeepaliveAsync(CancellationToken cancellationToken)
    {
        var refreshInterval = TimeSpan.FromSeconds(_options.EffectiveProcessingRefreshSeconds);

        try
        {
            using var timer = new PeriodicTimer(refreshInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_hasContent || _connectionDegraded)
                    return;

                await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (_hasContent || string.IsNullOrEmpty(_streamId) || _connectionDegraded)
                        return;

                    if (!CanSendNow())
                    {
                        _pendingOutbound = true;
                        return;
                    }

                    await _sender.ReplyStreamAsync(
                        _replyFrame,
                        _streamId,
                        ProcessingText,
                        finish: false,
                        ResolveLiveEpoch(),
                        cancellationToken,
                        WeComOutboundPriority.Keepalive).ConfigureAwait(false);
                }
                finally
                {
                    _sendLock.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // content arrived or stream completed
        }
    }

    private async Task StopKeepaliveAsync()
    {
        if (_keepaliveCts == null)
            return;

        _keepaliveCts.Cancel();
        if (_keepaliveTask != null)
        {
            try
            {
                await _keepaliveTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected
            }
        }

        _keepaliveCts.Dispose();
        _keepaliveCts = null;
        _keepaliveTask = null;
    }

    public async ValueTask DisposeAsync()
    {
        UnregisterActiveStream();
        await StopPublishLoopAsync().ConfigureAwait(false);
        await StopKeepaliveAsync().ConfigureAwait(false);
        _sendLock.Dispose();
    }

    private void RegisterActiveStream()
    {
        if (_registry == null || string.IsNullOrEmpty(_streamId))
            return;

        _registry.Register(_streamId, this);
    }

    private void UnregisterActiveStream()
    {
        if (_registry == null || string.IsNullOrEmpty(_streamId))
            return;

        _registry.Unregister(_streamId, this);
    }
}
