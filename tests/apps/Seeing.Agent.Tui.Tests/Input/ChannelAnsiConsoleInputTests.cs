using System.Threading.Channels;
using FluentAssertions;
using Seeing.Agent.Tui.Input;

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
