using System.Threading.Channels;

namespace Seeing.Agent.Tui.Input;

/// <summary>
/// 后台任务：把 <see cref="IRawInputSource"/> 的原始 token 经 <see cref="InputKeyDecoder"/>
/// 解码为 <see cref="TuiKeyInput"/> 泵入通道；维护 bracketed paste 状态。
/// </summary>
public sealed class TuiInputThread : IAsyncDisposable
{
    private readonly IRawInputSource _source;
    private readonly Channel<TuiKeyInput> _channel = Channel.CreateUnbounded<TuiKeyInput>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource _cts = new();

    private Task? _pumpTask;
    private volatile bool _pasteActive;
    private bool _started;
    private bool _disposed;

    public TuiInputThread(IRawInputSource source) => _source = source;

    public ChannelReader<TuiKeyInput> Reader => _channel.Reader;

    public async Task StartAsync(CancellationToken ct)
    {
        if (_started)
            return;

        _started = true;
        await _source.StartAsync(ct).ConfigureAwait(false);
        _pumpTask = Task.Run(PumpAsync);
    }

    public async Task StopAsync()
    {
        if (!_started)
        {
            _channel.Writer.TryComplete();
            return;
        }

        _started = false;
        _cts.Cancel();
        await _source.StopAsync().ConfigureAwait(false);

        var task = _pumpTask;
        if (task is not null)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常停止
            }
        }

        _channel.Writer.TryComplete();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        await _source.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var raw in _source.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                if (raw is TuiRawPasteStart)
                    _pasteActive = true;
                else if (raw is TuiRawPasteEnd)
                    _pasteActive = false;

                var key = InputKeyDecoder.Decode(raw, _pasteActive);
                await _channel.Writer.WriteAsync(key, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
        finally
        {
            _channel.Writer.TryComplete();
        }
    }
}
