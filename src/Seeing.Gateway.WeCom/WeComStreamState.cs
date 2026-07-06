namespace Seeing.Gateway.WeCom;

/// <summary>
/// 单条入站消息的企微流式回复。
/// <para>
/// 协议约束：每条用户消息仅一条 <c>stream_id</c>，且仅 <see cref="CompleteAsync"/> /
/// <see cref="FailAsync"/> 可发送 <c>finish=true</c>。处理中占位与正文共用同一 stream。
/// </para>
/// </summary>
public sealed class WeComStreamState : IAsyncDisposable
{
    private const string ProcessingText = "🤔 Thinking...";

    private readonly IWeComStreamSender _sender;
    private readonly WeComWsFrame _requestFrame;
    private readonly WeComOptions _options;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private string? _streamId;
    private string _visibleText = ProcessingText;
    private DateTime _lastDeltaSentUtc = DateTime.MinValue;
    private bool _hasContent;
    private bool _completed;
    private CancellationTokenSource? _keepaliveCts;
    private Task? _keepaliveTask;

    public WeComStreamState(
        WeComAibotWsClient client,
        WeComWsFrame requestFrame,
        WeComOptions options)
        : this(new WeComAibotStreamSender(client), requestFrame, options)
    {
    }

    internal WeComStreamState(
        IWeComStreamSender sender,
        WeComWsFrame requestFrame,
        WeComOptions options)
    {
        _sender = sender;
        _requestFrame = requestFrame;
        _options = options;
    }

    /// <summary>直接发送最终回复，不显示 Thinking 占位（用于斜杠命令等即时反馈）。</summary>
    public async Task SendInstantAsync(string text, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_completed, this);

        _streamId = WeComAibotWsClient.GenerateStreamId();
        _visibleText = FormatFinalText(text);
        await SendAsync(finish: true, cancellationToken).ConfigureAwait(false);
        _completed = true;
    }

    /// <summary>开启回复流并显示处理中占位（finish=false）。</summary>
    public async Task BeginAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_completed, this);

        _streamId = WeComAibotWsClient.GenerateStreamId();
        _visibleText = ProcessingText;
        await SendAsync(finish: false, cancellationToken).ConfigureAwait(false);

        _keepaliveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _keepaliveTask = RunProcessingKeepaliveAsync(_keepaliveCts.Token);
    }

    /// <summary>推送正文增量（finish=false）。</summary>
    public async Task PublishAsync(string text, CancellationToken cancellationToken)
    {
        if (!_options.StreamingEnabled || string.IsNullOrEmpty(text))
            return;

        ObjectDisposedException.ThrowIf(_completed, this);

        _hasContent = true;
        _visibleText = text;
        await StopKeepaliveAsync().ConfigureAwait(false);

        var throttleMs = Math.Max(0, _options.DeltaThrottleMilliseconds);
        var now = DateTime.UtcNow;
        if ((now - _lastDeltaSentUtc).TotalMilliseconds < throttleMs)
            return;

        await SendAsync(finish: false, cancellationToken).ConfigureAwait(false);
        _lastDeltaSentUtc = now;
    }

    /// <summary>发送最终正文并结束流（finish=true）。</summary>
    public async Task CompleteAsync(string? finalText, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_completed, this);

        await StopKeepaliveAsync().ConfigureAwait(false);

        _visibleText = FormatFinalText(finalText);
        await SendAsync(finish: true, cancellationToken).ConfigureAwait(false);
        _completed = true;
    }

    /// <summary>发送错误并结束流（finish=true）。</summary>
    public async Task FailAsync(string error, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_completed, this);

        await StopKeepaliveAsync().ConfigureAwait(false);
        _visibleText = $"❌ {error}";
        await SendAsync(finish: true, cancellationToken).ConfigureAwait(false);
        _completed = true;
    }

    private string FormatFinalText(string? finalText)
    {
        var text = string.IsNullOrWhiteSpace(finalText)
            ? (_hasContent ? _visibleText : "✅")
            : finalText;

        if (!string.IsNullOrWhiteSpace(_options.BotPrefix))
            text = $"{_options.BotPrefix}  {text}";

        return text;
    }

    private async Task SendAsync(bool finish, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_streamId))
            throw new InvalidOperationException("WeCom reply stream has not been started.");

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _sender.ReplyStreamAsync(
                _requestFrame,
                _streamId,
                _visibleText,
                finish,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task RunProcessingKeepaliveAsync(CancellationToken cancellationToken)
    {
        var refreshInterval = TimeSpan.FromSeconds(_options.EffectiveProcessingRefreshSeconds);

        try
        {
            using var timer = new PeriodicTimer(refreshInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_hasContent)
                    return;

                await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (_hasContent || string.IsNullOrEmpty(_streamId))
                        return;

                    await _sender.ReplyStreamAsync(
                        _requestFrame,
                        _streamId,
                        ProcessingText,
                        finish: false,
                        cancellationToken).ConfigureAwait(false);
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
        await StopKeepaliveAsync().ConfigureAwait(false);
        _sendLock.Dispose();
    }
}
