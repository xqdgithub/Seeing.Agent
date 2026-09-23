using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;

namespace Seeing.Agent.Tui.Input;

/// <summary>
/// 原始 stdin 字节读取（raw 模式 + bracketed paste），产出 <see cref="TuiRawInput"/>。
/// 说明：<see cref="Console.ReadKey"/> 无法承载 bracketed paste / CSI-u，故直接读字节流自行解析。
/// 启动发 <c>\x1b[?2004h</c>、退出发 <c>\x1b[?2004l</c>。
/// <para>
/// <c>mouseEnabled</c> 为真时在 bracketed paste 开关之后/同处追加/合并写
/// <c>\x1b[?1000;1003;1006h</c> 与 <c>\x1b[?1006;1003;1000l</c>（SGR 鼠标上报开关）；
/// 解析 <c>ESC[&lt;btn;col;row(M|m)</c> 产 <see cref="TuiRawMouse"/>，
/// 而 DSR 光标回复 <c>ESC[row;colR</c> 直接旁路给 <see cref="ITuiAnchorProbe"/>，绝不进按键通道。
/// </para>
/// </summary>
/// <remarks>
/// 平台边界：Windows 启用 <c>ENABLE_VIRTUAL_TERMINAL_INPUT</c>，停止时注入控制台哨兵事件解除阻塞读；
/// Linux 与 macOS 走 termios raw + <c>poll</c> 可取消读（各自平台结构/标志不同，分别适配）；
/// 其他 Unix（FreeBSD 等）退化为托管流读取，无法在 <see cref="StopAsync"/> 中立刻解除阻塞读
/// （读取线程为后台线程，随进程退出）。
/// <para>
/// 信号路径：Unix 不设置 <see cref="Console.TreatControlCAsInput"/>（该 API 在 Unix 抛异常），
/// raw 模式下 Ctrl+C 作为字节 <c>0x03</c> 由本读取器解析；SIGINT/SIGTERM 由 Host 生命周期统一处理
/// （引擎订阅 <c>ApplicationStopping</c> 取消主循环）。
/// </para>
/// <para>macOS 为<b>尽力支持</b>（无实机验证）：termios 结构与标志按 Darwin 定义，若失败则降级为流读取。</para>
/// </remarks>
public sealed class RawInputReader : IRawInputSource
{
    private const byte Esc = 0x1b;
    private const uint EnableProcessedInput = 0x0001;
    private const uint EnableLineInput = 0x0002;
    private const uint EnableEchoInput = 0x0004;
    private const uint EnableVirtualTerminalInput = 0x0200;
    private const int StdInputHandle = -10;

    private static readonly byte[] PasteStartSeq = "\x1b[200~"u8.ToArray();
    private static readonly byte[] PasteEndSeq = "\x1b[201~"u8.ToArray();

    private readonly Channel<TuiRawInput> _channel = Channel.CreateUnbounded<TuiRawInput>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _gate = new();
    private readonly List<byte> _pending = [];
    private readonly StringBuilder _textRun = new();
    private readonly UTF8Encoding _utf8 = new(false);
    private readonly bool _mouseEnabled;
    private readonly ITuiAnchorProbe? _anchorProbe;

    private Stream? _input;
    private Thread? _readThread;
    private volatile bool _stopRequested;
    private bool _started;
    private bool _inPaste;
    private bool _escapeFlushScheduled;
    private bool _disposed;
    private IntPtr _stdinHandle = IntPtr.Zero;
    private bool _consoleModeSaved;
    private uint _originalInputMode;
    private bool _treatControlCSaved;
    private bool _originalTreatControlC;
    private bool _unixRawEnabled;
    private bool _hasSavedLinuxTermios;
    private LinuxTermios _savedLinuxTermios;
    private bool _hasSavedMacTermios;
    private MacTermios _savedMacTermios;

    public ChannelReader<TuiRawInput> Reader => _channel.Reader;

    public RawInputReader(bool mouseEnabled = false, ITuiAnchorProbe? anchorProbe = null)
    {
        _mouseEnabled = mouseEnabled;
        _anchorProbe = anchorProbe;
    }

    public Task StartAsync(CancellationToken ct)
    {
        if (_started)
            return Task.CompletedTask;

        _started = true;
        _stopRequested = false;

        // 仅 Windows 使用 Console.TreatControlCAsInput（Unix 上该 API 抛异常）。
        // Unix 依赖 raw 模式下的 0x03 字节解析与 SIGINT/SIGTERM 信号路径。
        if (OperatingSystem.IsWindows())
            TrySetTreatControlCAsInput(true);

        if (OperatingSystem.IsWindows())
        {
            _input = Console.OpenStandardInput();
            _stdinHandle = GetStdHandle(StdInputHandle);
            if (_stdinHandle != IntPtr.Zero && GetConsoleMode(_stdinHandle, out var mode))
            {
                _originalInputMode = mode;
                _consoleModeSaved = true;
                // 必须同时关掉行缓冲与本地回显：否则控制台会把按键回显到当前光标处
                // （表现为输入“跑”到状态栏/活动区末尾，而不是我们的输入行），且按键要等回车才送达。
                var rawMode = (mode | EnableVirtualTerminalInput)
                    & ~(EnableLineInput | EnableEchoInput | EnableProcessedInput);
                _ = SetConsoleMode(_stdinHandle, rawMode);
            }
        }
        else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            // Linux 与 macOS 同为 Unix：走 termios raw + poll 可取消读（两平台结构/标志不同）。
            _unixRawEnabled = TryEnableUnixRawMode();
            _input = Console.OpenStandardInput();
        }
        else
        {
            // 其他 Unix：无 termios 适配，显式降级为流读取（不做静默兜底，见类备注）。
            _input = Console.OpenStandardInput();
        }

        TryWriteControlSequence("\x1b[?2004h");
        if (_mouseEnabled)
            TryWriteControlSequence("\x1b[?1000;1003;1006h");

        _readThread = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = "seeing-tui-input",
        };
        _readThread.Start();

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (!_started)
        {
            _channel.Writer.TryComplete();
            return;
        }

        _stopRequested = true;
        _cts.Cancel();

        if (OperatingSystem.IsWindows())
            TryDeliverWindowsStopSentinel();

        var thread = _readThread;
        if (thread is not null)
            await Task.Run(() => thread.Join(TimeSpan.FromMilliseconds(1500))).ConfigureAwait(false);

        RestoreConsole();

        TryWriteControlSequence(_mouseEnabled ? "\x1b[?2004l\x1b[?1006;1003;1000l" : "\x1b[?2004l");

        _channel.Writer.TryComplete();
        _started = false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        _cts.Dispose();
    }

    private void ReadLoop()
    {
        var buffer = new byte[4096];
        try
        {
            if ((OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) && _unixRawEnabled)
                UnixReadLoop(buffer);
            else
                StreamReadLoop(buffer);
        }
        catch (Exception)
        {
            // 停止/关闭导致的读取异常一律视为循环结束
        }
        finally
        {
            lock (_gate)
            {
                FlushTextRunLocked();
            }

            _channel.Writer.TryComplete();
        }
    }

    private void StreamReadLoop(byte[] buffer)
    {
        var input = _input!;
        while (!_stopRequested)
        {
            int read;
            try
            {
                read = input.Read(buffer, 0, buffer.Length);
            }
            catch (Exception)
            {
                break;
            }

            if (_stopRequested || read <= 0)
                break;

            AppendAndDrain(buffer, read);
        }
    }

    private void UnixReadLoop(byte[] buffer)
    {
        var fds = new[] { new PollFd { fd = 0, events = PollIn } };
        while (!_stopRequested)
        {
            var ready = poll(fds, 1, 100);
            if (ready < 0)
            {
                if (Marshal.GetLastPInvokeError() == Eintr)
                    continue;
                break;
            }

            if (ready == 0 || (fds[0].revents & PollIn) == 0)
                continue;

            var n = read(0, buffer, (nuint)buffer.Length);
            if (n <= 0)
            {
                if (n < 0 && Marshal.GetLastPInvokeError() == Eintr)
                    continue;
                break;
            }

            AppendAndDrain(buffer, (int)n);
        }
    }

    internal void AppendAndDrain(byte[] data, int count)
    {
        lock (_gate)
        {
            for (var i = 0; i < count; i++)
                _pending.Add(data[i]);

            DrainLocked();
            FlushTextRunLocked();
            ScheduleEscapeFlushLocked();
        }
    }

    private void DrainLocked()
    {
        while (_pending.Count > 0)
        {
            if (_inPaste)
            {
                DrainPasteLocked();
                return;
            }

            var b = _pending[0];
            if (b == Esc)
            {
                if (_pending.Count == 1)
                    return;

                var second = _pending[1];
                if (second == (byte)'[')
                {
                    var end = -1;
                    for (var i = 2; i < _pending.Count; i++)
                    {
                        if (_pending[i] is >= 0x40 and <= 0x7E)
                        {
                            end = i;
                            break;
                        }
                    }

                    if (end < 0)
                        return;

                    // SGR 鼠标：ESC [ < btn ; col ; row (M|m)，'<' 为参数字节后的私有前缀；
                    // 无法解析的鼠标序列直接丢弃，绝不落 TuiRawEscape/InsertText。
                    if (_pending[2] == (byte)'<')
                    {
                        var mouse = TryDecodeSgrMouse(CollectionsMarshal.AsSpan(_pending)[3..end], _pending[end]);
                        RemoveFrontLocked(end + 1);
                        if (mouse is not null)
                            EmitTokenLocked(mouse);
                        continue;
                    }

                    // DSR 光标位置回复：ESC [ row ; col R —— 旁路直达探针，不产 TuiRawInput、不写按键通道。
                    if (_pending[end] == (byte)'R' &&
                        TryParseDsrRow(CollectionsMarshal.AsSpan(_pending)[2..end], out var cursorRow))
                    {
                        RemoveFrontLocked(end + 1);
                        _anchorProbe?.Report(cursorRow);
                        continue;
                    }

                    var sequence = Encoding.ASCII.GetString(CollectionsMarshal.AsSpan(_pending)[..(end + 1)]);
                    RemoveFrontLocked(end + 1);
                    EmitEscapeLocked(sequence);
                    continue;
                }

                if (second == (byte)'O')
                {
                    if (_pending.Count < 3)
                        return;

                    var sequence = Encoding.ASCII.GetString(CollectionsMarshal.AsSpan(_pending)[..3]);
                    RemoveFrontLocked(3);
                    EmitEscapeLocked(sequence);
                    continue;
                }

                RemoveFrontLocked(1);
                EmitTokenLocked(new TuiRawKey(ConsoleKey.Escape, (char)Esc, false, false, false));
                continue;
            }

            if (b == 0x0D)
            {
                RemoveFrontLocked(1);
                EmitTokenLocked(new TuiRawKey(ConsoleKey.Enter, '\r', false, false, false));
                continue;
            }

            if (b == 0x0A)
            {
                RemoveFrontLocked(1);
                EmitTokenLocked(new TuiRawKey(ConsoleKey.Enter, '\n', false, false, false));
                continue;
            }

            if (b == 0x09)
            {
                RemoveFrontLocked(1);
                EmitTokenLocked(new TuiRawKey(ConsoleKey.Tab, '\t', false, false, false));
                continue;
            }

            if (b is 0x7F or 0x08)
            {
                RemoveFrontLocked(1);
                EmitTokenLocked(new TuiRawKey(ConsoleKey.Backspace, (char)b, false, false, false));
                continue;
            }

            if (b < 0x20)
            {
                RemoveFrontLocked(1);
                EmitControlLocked(b);
                continue;
            }

            if (b < 0x80)
            {
                var run = 0;
                while (run < _pending.Count && _pending[run] is >= 0x20 and < 0x80)
                    run++;

                _textRun.Append(Encoding.ASCII.GetString(CollectionsMarshal.AsSpan(_pending)[..run]));
                RemoveFrontLocked(run);
                continue;
            }

            var need = b < 0xE0 ? 2 : b < 0xF0 ? 3 : 4;
            if (_pending.Count < need)
                return;

            _textRun.Append(_utf8.GetString(CollectionsMarshal.AsSpan(_pending)[..need]));
            RemoveFrontLocked(need);
        }
    }

    private void DrainPasteLocked()
    {
        var index = IndexOfLocked(PasteEndSeq);
        if (index < 0)
        {
            // 与普通路径一致地按 UTF-8 字符边界截断：只输出完整字符，残缺尾部留待下轮，
            // 同时保留 PasteEndSeq.Length-1 字节以便下轮识别 \x1b[201~。
            var safe = PasteSafeEmitLength(CollectionsMarshal.AsSpan(_pending));
            if (safe > 0)
            {
                _textRun.Append(_utf8.GetString(CollectionsMarshal.AsSpan(_pending)[..safe]));
                RemoveFrontLocked(safe);
                FlushTextRunLocked();
            }

            return;
        }

        if (index > 0)
        {
            _textRun.Append(_utf8.GetString(CollectionsMarshal.AsSpan(_pending)[..index]));
            FlushTextRunLocked();
        }

        RemoveFrontLocked(index + PasteEndSeq.Length);
        _inPaste = false;
        EmitTokenLocked(new TuiRawPasteEnd());
    }

    /// <summary>
    /// 粘贴态下（未出现完整 <c>\x1b[201~</c> 时）可安全输出的字节数：
    /// 预留结束序列前缀后，再回退到最近的 UTF-8 字符边界，残缺尾部留待下轮。
    /// </summary>
    internal static int PasteSafeEmitLength(ReadOnlySpan<byte> pending)
    {
        var limit = pending.Length - (PasteEndSeq.Length - 1);
        if (limit <= 0)
            return 0;

        return Utf8CompletePrefixLength(pending[..limit]);
    }

    /// <summary>返回 <paramref name="bytes"/> 中构成完整 UTF-8 字符的最长前缀长度（残留尾序列不计入）。</summary>
    internal static int Utf8CompletePrefixLength(ReadOnlySpan<byte> bytes)
    {
        var i = 0;
        while (i < bytes.Length)
        {
            byte b = bytes[i];
            int need;
            if (b < 0x80)
                need = 1;
            else if ((b & 0xE0) == 0xC0)
                need = 2;
            else if ((b & 0xF0) == 0xE0)
                need = 3;
            else if ((b & 0xF8) == 0xF0)
                need = 4;
            else
                need = 1; // 非法起始字节：按单字节推进，交由 UTF-8 解码器回退处理

            if (i + need > bytes.Length)
                return i; // 尾部残缺，留待下轮

            i += need;
        }

        return bytes.Length;
    }

    /// <summary>在 <paramref name="haystack"/> 中查找 <paramref name="needle"/> 首次出现的下标，未找到返回 -1。</summary>
    internal static int IndexOfSequence(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.Slice(i, needle.Length).SequenceEqual(needle))
                return i;
        }

        return -1;
    }

    private void EmitEscapeLocked(string sequence)
    {
        switch (sequence)
        {
            case "\x1b[200~":
                EmitTokenLocked(new TuiRawPasteStart());
                _inPaste = true;
                break;
            case "\x1b[201~":
                EmitTokenLocked(new TuiRawPasteEnd());
                break;
            default:
                EmitTokenLocked(new TuiRawEscape(sequence));
                break;
        }
    }

    /// <summary>
    /// 解析 SGR 鼠标事件参数（<c>btn;col;row</c>）与终字节（M/m）。
    /// 解码严格按序：0x40（滚轮，恒 Press）→ 0x20（Motion）→ 按下/释放（M=Press、m=Release）；
    /// 无法解析返回 null（调用方丢弃该序列）。
    /// </summary>
    internal static TuiRawMouse? TryDecodeSgrMouse(ReadOnlySpan<byte> parameters, byte final)
    {
        if (final is not ((byte)'M' or (byte)'m'))
            return null;

        var firstSep = parameters.IndexOf((byte)';');
        if (firstSep < 0)
            return null;

        var rest = parameters[(firstSep + 1)..];
        var secondSep = rest.IndexOf((byte)';');
        if (secondSep < 0)
            return null;

        var btnSpan = parameters[..firstSep];
        var colSpan = rest[..secondSep];
        var rowSpan = rest[(secondSep + 1)..];
        if (rowSpan.Contains((byte)';'))
            return null;

        if (!TryParseDecimal(btnSpan, out var btn) ||
            !TryParseDecimal(colSpan, out var col) ||
            !TryParseDecimal(rowSpan, out var row))
            return null;

        TuiMouseButton button;
        TuiMousePhase phase;
        if ((btn & 0x40) != 0)
        {
            button = btn == 64 ? TuiMouseButton.WheelUp : TuiMouseButton.WheelDown;
            phase = TuiMousePhase.Press;
        }
        else if ((btn & 0x20) != 0)
        {
            button = MapMouseButton(btn & 0x07);
            phase = TuiMousePhase.Motion;
        }
        else
        {
            button = MapMouseButton(btn & 0x07);
            phase = final == (byte)'M' ? TuiMousePhase.Press : TuiMousePhase.Release;
        }

        return new TuiRawMouse(button, phase, col, row);
    }

    private static TuiMouseButton MapMouseButton(int code) => code switch
    {
        0 => TuiMouseButton.Left,
        1 => TuiMouseButton.Middle,
        2 => TuiMouseButton.Right,
        _ => TuiMouseButton.None,
    };

    /// <summary>解析 DSR 光标回复 <c>ESC [ row ; col R</c> 的参数段取 row（空段按 1 基默认）。</summary>
    private static bool TryParseDsrRow(ReadOnlySpan<byte> parameters, out int row)
    {
        row = 1;
        var sep = parameters.IndexOf((byte)';');
        if (sep < 0)
            return parameters.IsEmpty || TryParseDecimal(parameters, out row);

        var rowSpan = parameters[..sep];
        var colSpan = parameters[(sep + 1)..];
        if (colSpan.Contains((byte)';'))
            return false;

        if (!rowSpan.IsEmpty && !TryParseDecimal(rowSpan, out row))
            return false;

        return colSpan.IsEmpty || IsAllDecimal(colSpan);
    }

    private static bool TryParseDecimal(ReadOnlySpan<byte> digits, out int value)
    {
        value = 0;
        if (digits.IsEmpty)
            return false;

        foreach (var d in digits)
        {
            if (d is < (byte)'0' or > (byte)'9')
                return false;

            value = value * 10 + (d - '0');
        }

        return true;
    }

    private static bool IsAllDecimal(ReadOnlySpan<byte> digits)
    {
        foreach (var d in digits)
        {
            if (d is < (byte)'0' or > (byte)'9')
                return false;
        }

        return true;
    }

    private void EmitControlLocked(byte b)
    {
        if (b == 0x03)
        {
            EmitTokenLocked(new TuiRawKey(ConsoleKey.C, '\u0003', true, false, false));
            return;
        }

        if (b == 0x04)
        {
            EmitTokenLocked(new TuiRawKey(ConsoleKey.D, '\u0004', true, false, false));
            return;
        }

        if (b is >= 0x01 and <= 0x1A)
        {
            var key = (ConsoleKey)((int)ConsoleKey.A + (b - 1));
            EmitTokenLocked(new TuiRawKey(key, (char)('a' + b - 1), true, false, false));
            return;
        }

        EmitTokenLocked(new TuiRawKey(ConsoleKey.None, (char)b, false, false, false));
    }

    private void EmitTokenLocked(TuiRawInput input)
    {
        FlushTextRunLocked();
        _channel.Writer.TryWrite(input);
    }

    private void FlushTextRunLocked()
    {
        if (_textRun.Length == 0)
            return;

        var text = _textRun.ToString();
        _textRun.Clear();
        _channel.Writer.TryWrite(new TuiRawText(text));
    }

    private void ScheduleEscapeFlushLocked()
    {
        if (_pending.Count != 1 || _pending[0] != Esc)
        {
            _escapeFlushScheduled = false;
            return;
        }

        if (_escapeFlushScheduled)
            return;

        _escapeFlushScheduled = true;
        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(35, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            lock (_gate)
            {
                _escapeFlushScheduled = false;
                if (_pending.Count == 1 && _pending[0] == Esc && !_stopRequested)
                {
                    _pending.RemoveAt(0);
                    EmitTokenLocked(new TuiRawKey(ConsoleKey.Escape, (char)Esc, false, false, false));
                }
            }
        });
    }

    private int IndexOfLocked(byte[] needle)
    {
        for (var i = 0; i + needle.Length <= _pending.Count; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (_pending[i + j] == needle[j])
                    continue;

                match = false;
                break;
            }

            if (match)
                return i;
        }

        return -1;
    }

    private void RemoveFrontLocked(int count)
    {
        if (count <= 0)
            return;

        if (count >= _pending.Count)
            _pending.Clear();
        else
            _pending.RemoveRange(0, count);
    }

    private static void TryWriteControlSequence(string sequence)
    {
        try
        {
            Console.Out.Write(sequence);
            Console.Out.Flush();
        }
        catch (Exception)
        {
            // 输出被重定向时忽略
        }
    }

    private void TrySetTreatControlCAsInput(bool enabled)
    {
        try
        {
            if (enabled)
            {
                _originalTreatControlC = Console.TreatControlCAsInput;
                _treatControlCSaved = true;
            }

            Console.TreatControlCAsInput = enabled;
        }
        catch (Exception)
        {
            // 无控制台时忽略
        }
    }

    private void RestoreConsole()
    {
        if (OperatingSystem.IsWindows() && _consoleModeSaved && _stdinHandle != IntPtr.Zero)
        {
            _ = SetConsoleMode(_stdinHandle, _originalInputMode);
            _consoleModeSaved = false;
        }

        if (OperatingSystem.IsLinux() && _unixRawEnabled && _hasSavedLinuxTermios)
        {
            var termios = _savedLinuxTermios;
            _ = tcsetattr(0, Tcsanow, ref termios);
            _unixRawEnabled = false;
        }
        else if (OperatingSystem.IsMacOS() && _unixRawEnabled && _hasSavedMacTermios)
        {
            var termios = _savedMacTermios;
            _ = tcsetattrMac(0, Tcsanow, ref termios);
            _unixRawEnabled = false;
        }

        if (_treatControlCSaved)
        {
            TrySetTreatControlCAsInput(_originalTreatControlC);
            _treatControlCSaved = false;
        }
    }

    private void TryDeliverWindowsStopSentinel()
    {
        if (_stdinHandle == IntPtr.Zero)
            return;

        try
        {
            var record = new InputRecord
            {
                EventType = 0x0001,
                KeyEvent = new KeyEventRecord
                {
                    KeyDown = 1,
                    RepeatCount = 1,
                    UnicodeChar = '\0',
                },
            };
            _ = WriteConsoleInputW(_stdinHandle, [record], 1, out _);
        }
        catch (Exception)
        {
            // 非控制台句柄：忽略
        }

        try
        {
            _ = CancelIoEx(_stdinHandle, IntPtr.Zero);
        }
        catch (Exception)
        {
            // 尽力而为
        }
    }

    /// <summary>按平台启用 Unix raw 模式；macOS 为尽力支持（未经实机验证）。</summary>
    private bool TryEnableUnixRawMode()
        => OperatingSystem.IsMacOS() ? TryEnableMacRawMode() : TryEnableLinuxRawMode();

    private bool TryEnableLinuxRawMode()
    {
        try
        {
            if (tcgetattr(0, out var original) != 0)
                return false;

            var raw = original;
            raw.c_cc = (byte[])original.c_cc.Clone();
            raw.c_lflag &= ~(Icanon | Echo | Isig | Iexten);
            raw.c_iflag &= ~(Ixon | Icrnl | Brkint | Inpck | Istrip);
            raw.c_oflag &= ~Opost;
            raw.c_cc[Vmin] = 1;
            raw.c_cc[Vtime] = 0;

            if (tcsetattr(0, Tcsanow, ref raw) != 0)
                return false;

            _savedLinuxTermios = original;
            _hasSavedLinuxTermios = true;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// macOS（Darwin）raw 模式：结构与 Linux 不同——<c>tcflag_t</c> 为 8 字节，<c>NCCS=20</c>，
    /// 且无 <c>c_line</c>；标志位与 VMIN/VTIME 下标亦不同。以上均按 Darwin <c>sys/termios.h</c> 定义，
    /// 未经实机验证；失败时返回 false 并降级为流读取。
    /// </summary>
    private bool TryEnableMacRawMode()
    {
        try
        {
            if (tcgetattrMac(0, out var original) != 0)
                return false;

            var raw = original;
            raw.c_cc = (byte[])original.c_cc.Clone();
            raw.c_lflag &= ~(MacIcanon | MacEcho | MacIsig | MacIexten);
            raw.c_iflag &= ~(MacIxon | MacIcrnl | MacBrkint | MacInpck | MacIstrip);
            raw.c_oflag &= ~MacOpost;
            raw.c_cc[MacVmin] = 1;
            raw.c_cc[MacVtime] = 0;

            if (tcsetattrMac(0, Tcsanow, ref raw) != 0)
                return false;

            _savedMacTermios = original;
            _hasSavedMacTermios = true;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private const int PollIn = 0x0001;
    private const int Eintr = 4;
    private const int Tcsanow = 0;
    private const int Vmin = 6;
    private const int Vtime = 5;

    private const uint Icrnl = 0x100;
    private const uint Ixon = 0x400;
    private const uint Brkint = 0x2;
    private const uint Inpck = 0x10;
    private const uint Istrip = 0x20;
    private const uint Opost = 0x1;
    private const uint Isig = 0x1;
    private const uint Icanon = 0x2;
    private const uint Echo = 0x8;
    private const uint Iexten = 0x8000;

    // Darwin（macOS）标志位与 VMIN/VTIME 下标：数值与 Linux 不同，切勿混用。
    private const int MacVmin = 16;
    private const int MacVtime = 17;
    private const uint MacIcrnl = 0x100;
    private const uint MacIxon = 0x200;
    private const uint MacBrkint = 0x2;
    private const uint MacInpck = 0x10;
    private const uint MacIstrip = 0x20;
    private const uint MacOpost = 0x1;
    private const uint MacIsig = 0x80;
    private const uint MacIcanon = 0x100;
    private const uint MacEcho = 0x8;
    private const uint MacIexten = 0x400;

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int fd;
        public short events;
        public short revents;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InputRecord
    {
        public ushort EventType;
        public KeyEventRecord KeyEvent;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyEventRecord
    {
        public int KeyDown;
        public ushort RepeatCount;
        public ushort VirtualKeyCode;
        public ushort VirtualScanCode;
        public char UnicodeChar;
        public uint ControlKeyState;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxTermios
    {
        public uint c_iflag;
        public uint c_oflag;
        public uint c_cflag;
        public uint c_lflag;
        public byte c_line;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] c_cc;
        public uint c_ispeed;
        public uint c_ospeed;
    }

    /// <summary>
    /// Darwin（macOS）<c>struct termios</c>：<c>tcflag_t</c>/<c>speed_t</c> 为 8 字节，<c>NCCS=20</c>，无 <c>c_line</c>。
    /// 布局与 <see cref="LinuxTermios"/> 不同，必须分开 P/Invoke，避免结构尺寸不符导致内存越界。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MacTermios
    {
        public ulong c_iflag;
        public ulong c_oflag;
        public ulong c_cflag;
        public ulong c_lflag;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 20)]
        public byte[] c_cc;
        public ulong c_ispeed;
        public ulong c_ospeed;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(IntPtr hFile, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WriteConsoleInputW(
        IntPtr hConsoleInput,
        [In] InputRecord[] lpBuffer,
        uint nLength,
        out uint lpNumberOfEventsWritten);

    // "libc" 在 Linux 直接解析；macOS 上 dyld 将 "libc" 作为 libSystem 的别名解析，故同一库名跨平台可用。
    [DllImport("libc", SetLastError = true)]
    private static extern int tcgetattr(int fd, out LinuxTermios termios);

    [DllImport("libc", SetLastError = true)]
    private static extern int tcsetattr(int fd, int optionalActions, ref LinuxTermios termios);

    // macOS 专用重载：结构不同，用 EntryPoint 绑定同一符号。
    [DllImport("libc", SetLastError = true, EntryPoint = "tcgetattr")]
    private static extern int tcgetattrMac(int fd, out MacTermios termios);

    [DllImport("libc", SetLastError = true, EntryPoint = "tcsetattr")]
    private static extern int tcsetattrMac(int fd, int optionalActions, ref MacTermios termios);

    // poll/read 的布局在 Linux 与 macOS 一致，共用声明。
    [DllImport("libc", SetLastError = true)]
    private static extern int poll([In, Out] PollFd[] fds, uint nfds, int timeout);

    [DllImport("libc", SetLastError = true)]
    private static extern nint read(int fd, byte[] buf, nuint count);
}
