namespace Seeing.Gateway.WeCom;

/// <summary>
/// 单条入站消息的流式回复状态（stream_id / keepalive / delta 节流）
/// </summary>
public sealed class WeComStreamState : IAsyncDisposable
{
    private const string ProcessingText = "🤔 Thinking...";

    private readonly IWeComStreamSender _sender;
    private readonly WeComWsFrame _requestFrame;
    private readonly WeComOptions _options;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private string? _contentStreamId;
    private string? _processingStreamId;
    private string _accumulatedText = string.Empty;
    private DateTime _lastDeltaSentUtc = DateTime.MinValue;
    private bool _contentPhaseStarted;
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

    public async Task StartProcessingIndicatorAsync(CancellationToken cancellationToken)
    {
        _processingStreamId = WeComAibotWsClient.GenerateStreamId();
        await _sender.SendProcessingIndicatorAsync(_requestFrame, _processingStreamId, cancellationToken)
            .ConfigureAwait(false);

        _keepaliveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _keepaliveTask = KeepaliveProcessingAsync(_processingStreamId, _keepaliveCts.Token);
    }

    public async Task UpdateContentDeltaAsync(string accumulatedText, CancellationToken cancellationToken)
    {
        if (!_options.StreamingEnabled)
            return;

        _accumulatedText = accumulatedText;
        await EnterContentPhaseAsync(cancellationToken).ConfigureAwait(false);

        var throttleMs = Math.Max(0, _options.DeltaThrottleMilliseconds);
        var now = DateTime.UtcNow;
        if ((now - _lastDeltaSentUtc).TotalMilliseconds < throttleMs)
            return;

        await SendContentAsync(finish: false, cancellationToken).ConfigureAwait(false);
        _lastDeltaSentUtc = now;
    }

    public async Task FinishAsync(string? finalText, CancellationToken cancellationToken)
    {
        await CancelKeepaliveAsync().ConfigureAwait(false);

        var text = finalText ?? _accumulatedText;
        if (!string.IsNullOrWhiteSpace(_options.BotPrefix))
            text = $"{_options.BotPrefix}  {text}";

        if (string.IsNullOrWhiteSpace(text))
            text = "✅";

        _accumulatedText = text;
        await SendContentAsync(finish: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendErrorAsync(string error, CancellationToken cancellationToken)
    {
        await CancelKeepaliveAsync().ConfigureAwait(false);
        _accumulatedText = $"❌ {error}";
        await SendContentAsync(finish: true, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnterContentPhaseAsync(CancellationToken cancellationToken)
    {
        if (_contentPhaseStarted)
            return;

        _contentPhaseStarted = true;
        await CancelKeepaliveAsync().ConfigureAwait(false);
    }

    private async Task SendContentAsync(bool finish, CancellationToken cancellationToken)
    {
        await EnterContentPhaseAsync(cancellationToken).ConfigureAwait(false);

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (string.IsNullOrEmpty(_contentStreamId))
            {
                if (!string.IsNullOrEmpty(_processingStreamId))
                {
                    _contentStreamId = _processingStreamId;
                    _processingStreamId = null;
                }
                else
                {
                    _contentStreamId = WeComAibotWsClient.GenerateStreamId();
                }
            }

            await _sender.ReplyStreamAsync(
                _requestFrame,
                _contentStreamId,
                _accumulatedText,
                finish,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task SendProcessingRefreshAsync(
        string streamId,
        bool finish,
        CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_contentPhaseStarted)
                return;

            await _sender.ReplyStreamAsync(
                _requestFrame,
                streamId,
                ProcessingText,
                finish,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task KeepaliveProcessingAsync(string streamId, CancellationToken cancellationToken)
    {
        var refreshInterval = TimeSpan.FromSeconds(Math.Max(5, _options.ProcessingRefreshSeconds));
        var maxDuration = TimeSpan.FromSeconds(Math.Max(refreshInterval.TotalSeconds, _options.ProcessingMaxDurationSeconds));
        var elapsed = TimeSpan.Zero;

        try
        {
            while (elapsed + refreshInterval <= maxDuration && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(refreshInterval, cancellationToken).ConfigureAwait(false);
                elapsed += refreshInterval;

                await SendProcessingRefreshAsync(streamId, finish: false, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                await SendProcessingRefreshAsync(streamId, finish: true, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // expected when real content arrives
        }
    }

    private async Task CancelKeepaliveAsync()
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
        await CancelKeepaliveAsync().ConfigureAwait(false);
        _sendLock.Dispose();
    }
}
