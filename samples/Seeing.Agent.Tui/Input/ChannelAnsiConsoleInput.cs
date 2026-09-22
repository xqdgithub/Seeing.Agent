using System.Threading.Channels;
using Spectre.Console;

namespace Seeing.Agent.Tui.Input;

/// <summary>
/// 把 <see cref="TuiInputThread"/> 解码后的 <see cref="TuiKeyInput"/> 适配为 Spectre 的
/// <see cref="IAnsiConsoleInput"/>，使权限/问答提示从 TUI 按键通道读取，
/// 而非与 <see cref="RawInputReader"/> 争抢同一 stdin。
/// </summary>
/// <remarks>
/// Spectre 提示内部以 <c>await console.Input.ReadKeyAsync(true, ct)</c> 逐键驱动（见 0.57.2
/// <c>ListPrompt</c> / <c>TextPrompt.ReadLine</c>），故只需实现三个接口成员即可。
/// </remarks>
public sealed class ChannelAnsiConsoleInput : IAnsiConsoleInput
{
    private readonly Queue<ConsoleKeyInfo> _buffer = new();
    private readonly Lock _gate = new();

    private ChannelReader<TuiKeyInput>? _reader;
    private CancellationToken _ct = CancellationToken.None;

    /// <summary>绑定按键通道与取消令牌（由 <see cref="TuiPromptInputRelay"/> 启动后调用）。</summary>
    public void Bind(ChannelReader<TuiKeyInput> reader, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = reader;
        _ct = ct;
    }

    /// <summary>清空尚未被 Spectre 消费的缓冲按键（进入提示前丢弃残留）。</summary>
    public void ClearBuffered()
    {
        lock (_gate)
            _buffer.Clear();
    }

    /// <inheritdoc />
    public bool IsKeyAvailable()
    {
        lock (_gate)
        {
            DrainLocked();
            return _buffer.Count > 0;
        }
    }

    /// <inheritdoc />
    public ConsoleKeyInfo? ReadKey(bool intercept)
    {
        while (true)
        {
            var key = TryTakeKey();
            if (key is not null)
                return key;

            bool available;
            try
            {
                available = Reader.WaitToReadAsync(_ct).AsTask().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                // 取消即视为无按键，避免把 OCE 抛给 Spectre 提示。
                return null;
            }

            if (!available)
                return null;
        }
    }

    /// <inheritdoc />
    public async Task<ConsoleKeyInfo?> ReadKeyAsync(bool intercept, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
                return null;

            var key = TryTakeKey();
            if (key is not null)
                return key;

            bool available;
            try
            {
                available = await Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            if (!available)
                return null;
        }
    }

    private ChannelReader<TuiKeyInput> Reader
        => _reader ?? throw new InvalidOperationException("ChannelAnsiConsoleInput 尚未绑定按键通道。");

    private ConsoleKeyInfo? TryTakeKey()
    {
        lock (_gate)
        {
            DrainLocked();
            return _buffer.Count > 0 ? _buffer.Dequeue() : null;
        }
    }

    private void DrainLocked()
    {
        if (_buffer.Count > 0 || _reader is null)
            return;

        while (_reader.TryRead(out var key))
        {
            MapLocked(key);
            if (_buffer.Count > 0)
                return;
        }
    }

    private void MapLocked(TuiKeyInput key)
    {
        switch (key.Action)
        {
            case TuiInputAction.None:
            case TuiInputAction.ExitRequested:
                return;

            case TuiInputAction.Submit:
                _buffer.Enqueue(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false));
                return;

            case TuiInputAction.InsertNewline:
                _buffer.Enqueue(new ConsoleKeyInfo('\n', ConsoleKey.J, false, false, false));
                return;

            case TuiInputAction.Backspace:
                _buffer.Enqueue(new ConsoleKeyInfo('\b', ConsoleKey.Backspace, false, false, false));
                return;

            case TuiInputAction.DeleteForward:
                _buffer.Enqueue(new ConsoleKeyInfo('\0', ConsoleKey.Delete, false, false, false));
                return;

            case TuiInputAction.MoveLeft:
                _buffer.Enqueue(new ConsoleKeyInfo('\0', ConsoleKey.LeftArrow, false, false, false));
                return;

            case TuiInputAction.MoveRight:
                _buffer.Enqueue(new ConsoleKeyInfo('\0', ConsoleKey.RightArrow, false, false, false));
                return;

            case TuiInputAction.HistoryPrev:
                _buffer.Enqueue(new ConsoleKeyInfo('\0', ConsoleKey.UpArrow, false, false, false));
                return;

            case TuiInputAction.HistoryNext:
                _buffer.Enqueue(new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, false, false, false));
                return;

            case TuiInputAction.Complete:
                _buffer.Enqueue(new ConsoleKeyInfo('\t', ConsoleKey.Tab, false, false, false));
                return;

            case TuiInputAction.Cancel:
                _buffer.Enqueue(new ConsoleKeyInfo('\u001b', ConsoleKey.Escape, false, false, false));
                return;

            case TuiInputAction.InsertText:
                foreach (var ch in key.Text)
                    _buffer.Enqueue(CreatePrintable(ch));
                return;
        }
    }

    private static ConsoleKeyInfo CreatePrintable(char ch)
    {
        var shift = char.IsUpper(ch);
        var consoleKey = char.IsLetter(ch)
            ? (ConsoleKey)char.ToUpperInvariant(ch)
            : ch == ' ' ? ConsoleKey.Spacebar : ConsoleKey.None;
        return new ConsoleKeyInfo(ch, consoleKey, shift, false, false);
    }
}

/// <summary>
/// 输入多路复用：单一读取 <see cref="TuiInputThread"/> 的按键通道，
/// 按「提示是否活跃」分发到引擎通道或提示通道，避免主循环与 Spectre 提示并发争抢按键。
/// </summary>
public sealed class TuiPromptInputRelay
{
    // 引擎通道存在两个逻辑写入者（分发泵 + EndPrompt 回投），故不声明 SingleWriter。
    private readonly Channel<TuiKeyInput> _engine = Channel.CreateUnbounded<TuiKeyInput>(
        new UnboundedChannelOptions { SingleReader = true });
    // _prompt 存在多个读者：Spectre 提示经 ChannelAnsiConsoleInput 读取，BeginPrompt/EndPrompt 还会
    // 在提示切换边界排空/回投同一 reader，故不得声明 SingleReader=true（否则并发读属未定义行为，理论丢键）。
    // 写入者仍只有分发泵 PumpAsync，SingleWriter=true 成立。
    private readonly Channel<TuiKeyInput> _prompt = Channel.CreateUnbounded<TuiKeyInput>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });

    private readonly Lock _gate = new();

    private int _promptDepth;
    private Task? _pump;

    public TuiPromptInputRelay() => Input = new ChannelAnsiConsoleInput();

    /// <summary>提示输入适配器（注入 Spectre <c>IAnsiConsole</c> 的 <c>Input</c>）。</summary>
    public ChannelAnsiConsoleInput Input { get; }

    /// <summary>引擎主循环读取的通道。</summary>
    public ChannelReader<TuiKeyInput> EngineReader => _engine.Reader;

    /// <summary>挂接上游按键源并启动分发泵（幂等）。</summary>
    public void Attach(ChannelReader<TuiKeyInput> source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (_pump is not null)
            return;

        Input.Bind(_prompt.Reader, ct);
        _pump = Task.Run(() => PumpAsync(source, ct), CancellationToken.None);
    }

    /// <summary>
    /// 进入提示模式：置标志与排空提示通道在同一临界区内完成（支持嵌套，内层结束不退出提示模式）。
    /// </summary>
    public void BeginPrompt()
    {
        lock (_gate)
        {
            _promptDepth++;
            // 丢弃进入提示前的陈旧按键（提示通道残留 + Spectre 内部缓冲）。
            while (_prompt.Reader.TryRead(out _))
            {
            }

            Input.ClearBuffered();
        }
    }

    /// <summary>
    /// 退出提示模式：最外层退出时把残留在提示通道的按键转投回引擎通道，避免丢键（非阻塞）。
    /// </summary>
    /// <remarks>
    /// Spectre 内部缓冲（<see cref="ChannelAnsiConsoleInput"/> 的 <c>ConsoleKeyInfo</c> 队列）里
    /// 提示未消费的按键在退出时**显式丢弃**，而不是留给下一个提示：这些按键是用户在提示期间按下的，
    /// 语义上属于该提示；且 <c>ConsoleKeyInfo → TuiKeyInput</c> 为有损反向映射，回投会篡改按键语义。
    /// </remarks>
    public void EndPrompt()
    {
        lock (_gate)
        {
            if (_promptDepth > 0)
                _promptDepth--;

            if (_promptDepth > 0)
                return;

            while (_prompt.Reader.TryRead(out var key))
                _engine.Writer.TryWrite(key);

            Input.ClearBuffered();
        }
    }

    private async Task PumpAsync(ChannelReader<TuiKeyInput> source, CancellationToken ct)
    {
        try
        {
            await foreach (var key in source.ReadAllAsync(ct).ConfigureAwait(false))
            {
                // 与 BeginPrompt/EndPrompt 共用锁：标志判定与写入原子化，消除过渡窗口的误投。
                lock (_gate)
                {
                    var destination = _promptDepth > 0 ? _prompt : _engine;
                    destination.Writer.TryWrite(key);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
        finally
        {
            _engine.Writer.TryComplete();
            _prompt.Writer.TryComplete();
        }
    }
}
