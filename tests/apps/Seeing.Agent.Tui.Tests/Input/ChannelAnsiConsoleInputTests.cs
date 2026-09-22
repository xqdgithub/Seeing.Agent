using System.Threading.Channels;
using FluentAssertions;
using Seeing.Agent.Tui.Input;
using Seeing.Agent.Tui.Rendering;
using Spectre.Console;

namespace Seeing.Agent.Tui.Tests.Input;

public class ChannelAnsiConsoleInputTests
{
    private static readonly (string Name, TuiKeyInput Input, ConsoleKeyInfo? Expected)[] Cases =
    [
        ("Enter 提交", new TuiKeyInput(TuiInputAction.Submit), new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false)),
        ("插入换行(J)", new TuiKeyInput(TuiInputAction.InsertNewline), new ConsoleKeyInfo('\n', ConsoleKey.J, false, false, false)),
        ("退格", new TuiKeyInput(TuiInputAction.Backspace), new ConsoleKeyInfo('\b', ConsoleKey.Backspace, false, false, false)),
        ("前删", new TuiKeyInput(TuiInputAction.DeleteForward), new ConsoleKeyInfo('\0', ConsoleKey.Delete, false, false, false)),
        ("左移", new TuiKeyInput(TuiInputAction.MoveLeft), new ConsoleKeyInfo('\0', ConsoleKey.LeftArrow, false, false, false)),
        ("右移", new TuiKeyInput(TuiInputAction.MoveRight), new ConsoleKeyInfo('\0', ConsoleKey.RightArrow, false, false, false)),
        ("上箭头", new TuiKeyInput(TuiInputAction.HistoryPrev), new ConsoleKeyInfo('\0', ConsoleKey.UpArrow, false, false, false)),
        ("下箭头", new TuiKeyInput(TuiInputAction.HistoryNext), new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, false, false, false)),
        ("Tab 补全", new TuiKeyInput(TuiInputAction.Complete), new ConsoleKeyInfo('\t', ConsoleKey.Tab, false, false, false)),
        ("Esc 取消", new TuiKeyInput(TuiInputAction.Cancel), new ConsoleKeyInfo('\u001b', ConsoleKey.Escape, false, false, false)),
        ("小写字母", new TuiKeyInput(TuiInputAction.InsertText, "a"), new ConsoleKeyInfo('a', ConsoleKey.A, false, false, false)),
        ("大写字母", new TuiKeyInput(TuiInputAction.InsertText, "A"), new ConsoleKeyInfo('A', ConsoleKey.A, true, false, false)),
        ("空格", new TuiKeyInput(TuiInputAction.InsertText, " "), new ConsoleKeyInfo(' ', ConsoleKey.Spacebar, false, false, false)),
        ("数字", new TuiKeyInput(TuiInputAction.InsertText, "1"), new ConsoleKeyInfo('1', ConsoleKey.None, false, false, false)),
        ("中文汉字", new TuiKeyInput(TuiInputAction.InsertText, "北"), new ConsoleKeyInfo('北', ConsoleKey.None, false, false, false)),
        ("全角符号", new TuiKeyInput(TuiInputAction.InsertText, "："), new ConsoleKeyInfo('：', ConsoleKey.None, false, false, false)),
        ("无动作不产出按键", new TuiKeyInput(TuiInputAction.None), null),
        ("退出请求不产出按键", new TuiKeyInput(TuiInputAction.ExitRequested), null),
    ];

    [Fact]
    public async Task ReadKeyAsync_ShouldMapTuiKeyToConsoleKeyInfo()
    {
        foreach (var (name, input, expected) in Cases)
        {
            var (channelInput, source) = CreateBound();
            source.Writer.TryWrite(input);

            if (expected is null)
            {
                channelInput.IsKeyAvailable().Should().BeFalse(name);
                continue;
            }

            var actual = await channelInput.ReadKeyAsync(true, TestContext.Current.CancellationToken);
            actual.Should().Be(expected.Value, name);
        }
    }

    [Fact]
    public async Task InsertText_WithCjk_ShouldNotThrowAndPreserveKeyChar()
    {
        // 回归：曾因 (ConsoleKey)char.ToUpperInvariant(ch) 对 CJK 产生 >255 的 key，
        // ConsoleKeyInfo 构造抛 ArgumentOutOfRangeException，中文输入直接崩掉引擎。
        var (channelInput, source) = CreateBound();
        source.Writer.TryWrite(new TuiKeyInput(TuiInputAction.InsertText, "北京 42℃"));

        var chars = new List<char>();
        for (var i = 0; i < "北京 42℃".Length; i++)
        {
            var key = await channelInput.ReadKeyAsync(true, TestContext.Current.CancellationToken);
            key.Should().NotBeNull();
            key!.Value.Key.Should().BeOneOf(ConsoleKey.None, ConsoleKey.Spacebar);
            chars.Add(key.Value.KeyChar);
        }

        new string(chars.ToArray()).Should().Be("北京 42℃");
    }

    [Fact]
    public async Task InsertText_ShouldEmitOneKeyPerCharacter()
    {
        var (channelInput, source) = CreateBound();
        source.Writer.TryWrite(new TuiKeyInput(TuiInputAction.InsertText, "ab"));

        var first = await channelInput.ReadKeyAsync(true, TestContext.Current.CancellationToken);
        var second = await channelInput.ReadKeyAsync(true, TestContext.Current.CancellationToken);

        first!.Value.KeyChar.Should().Be('a');
        second!.Value.KeyChar.Should().Be('b');
    }

    [Fact]
    public async Task ReadKeyAsync_ShouldReturnNullWhenCancelled()
    {
        var (channelInput, _) = CreateBound();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await channelInput.ReadKeyAsync(true, cts.Token);

        result.Should().BeNull();
    }

    [Fact]
    public void ReadKey_ShouldReturnNullWhenCancelled()
    {
        var source = Channel.CreateUnbounded<TuiKeyInput>();
        var input = new ChannelAnsiConsoleInput();
        using var cts = new CancellationTokenSource();
        input.Bind(source.Reader, cts.Token);
        cts.Cancel();

        var result = input.ReadKey(true);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Relay_ShouldRouteKeysByPromptState()
    {
        var source = Channel.CreateUnbounded<TuiKeyInput>();
        var relay = new TuiPromptInputRelay();
        using var cts = new CancellationTokenSource();
        relay.Attach(source.Reader, cts.Token);

        source.Writer.TryWrite(new TuiKeyInput(TuiInputAction.InsertText, "e"));
        var engineKey = await ReadWithTimeoutAsync(relay.EngineReader, TimeSpan.FromSeconds(2));
        engineKey!.Value.Text.Should().Be("e");

        relay.BeginPrompt();
        source.Writer.TryWrite(new TuiKeyInput(TuiInputAction.Submit));
        var promptKey = await relay.Input.ReadKeyAsync(true, cts.Token);

        promptKey!.Value.Key.Should().Be(ConsoleKey.Enter);
        relay.EndPrompt();
    }

    [Fact]
    public async Task Relay_BeginPrompt_ShouldDiscardStaleKeys()
    {
        var source = Channel.CreateUnbounded<TuiKeyInput>();
        var relay = new TuiPromptInputRelay();
        using var cts = new CancellationTokenSource();
        relay.Attach(source.Reader, cts.Token);

        relay.BeginPrompt();
        source.Writer.TryWrite(new TuiKeyInput(TuiInputAction.Submit));
        await WaitUntilAsync(() => relay.Input.IsKeyAvailable(), TimeSpan.FromSeconds(2));
        relay.EndPrompt();

        relay.BeginPrompt();

        relay.Input.IsKeyAvailable().Should().BeFalse();
    }

    [Fact]
    public async Task EscapePressed_ShouldFireOncePerEscapeKey()
    {
        // TextPrompt 忽略 Esc，渲染端依赖该信号取消提示令牌；触发时机必须在锁外且与按键一一对应。
        var (channelInput, source) = CreateBound();
        var fired = 0;
        channelInput.EscapePressed += () => fired++;

        source.Writer.TryWrite(new TuiKeyInput(TuiInputAction.InsertText, "a"));
        await channelInput.ReadKeyAsync(true, TestContext.Current.CancellationToken);
        fired.Should().Be(0);

        source.Writer.TryWrite(new TuiKeyInput(TuiInputAction.Cancel));
        await channelInput.ReadKeyAsync(true, TestContext.Current.CancellationToken);
        fired.Should().Be(1);

        // 非 Esc 按键不得触发
        source.Writer.TryWrite(new TuiKeyInput(TuiInputAction.InsertText, "b"));
        await channelInput.ReadKeyAsync(true, TestContext.Current.CancellationToken);
        fired.Should().Be(1);
    }

    [Fact]
    public async Task EscapePressed_ShouldCancelPromptCancellationSource()
    {
        var (channelInput, source) = CreateBound();
        using var cancellation = new CancellationTokenSource();
        using var scope = EscapeCancellationScope.Attach(channelInput, cancellation);

        source.Writer.TryWrite(new TuiKeyInput(TuiInputAction.Cancel));
        await channelInput.ReadKeyAsync(true, TestContext.Current.CancellationToken);

        cancellation.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task TextPrompt_WithCjkKeystrokes_ShouldReturnCjkAnswer()
    {
        // 端到端复现崩溃路径：QuestionPrompt → TextPrompt.ShowAsync 读取 TUI 按键通道。
        // 修复前：输入「北京」时 CreatePrintable 抛 ArgumentOutOfRangeException，引擎整体退出。
        var source = Channel.CreateUnbounded<TuiKeyInput>();
        var input = new ChannelAnsiConsoleInput();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        input.Bind(source.Reader, cts.Token);

        var writer = new StringWriter();
        var inner = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.Yes,
            Out = new AnsiConsoleOutput(writer),
        });
        var console = new InputOverrideConsole(inner, input);

        source.Writer.TryWrite(new TuiKeyInput(TuiInputAction.InsertText, "北京"));
        source.Writer.TryWrite(new TuiKeyInput(TuiInputAction.Submit));

        var answer = await new TextPrompt<string>("要查哪个城市的天气?").ShowAsync(console, cts.Token);

        answer.Should().Be("北京");
    }

    private static (ChannelAnsiConsoleInput Input, Channel<TuiKeyInput> Source) CreateBound()
    {
        var source = Channel.CreateUnbounded<TuiKeyInput>();
        var input = new ChannelAnsiConsoleInput();
        input.Bind(source.Reader, CancellationToken.None);
        return (input, source);
    }

    private static async Task<TuiKeyInput?> ReadWithTimeoutAsync(ChannelReader<TuiKeyInput> reader, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            return await reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;

            await Task.Delay(10);
        }
    }
}
