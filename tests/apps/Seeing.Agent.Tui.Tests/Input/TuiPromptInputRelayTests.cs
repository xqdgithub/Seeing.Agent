using System.Threading.Channels;
using FluentAssertions;
using Seeing.Agent.Tui.Input;

namespace Seeing.Agent.Tui.Tests.Input;

public class TuiPromptInputRelayTests
{
    [Fact]
    public async Task BeginPrompt_ShouldRouteToPrompt_EndPrompt_ShouldRouteToEngine()
    {
        var source = Channel.CreateUnbounded<TuiKeyInput>();
        var relay = new TuiPromptInputRelay();
        using var cts = new CancellationTokenSource();
        relay.Attach(source.Reader, cts.Token);

        relay.BeginPrompt();
        source.Writer.TryWrite(new TuiKeyInput(TuiInputAction.InsertText, "p"));
        var promptKey = await ReadPromptWithTimeoutAsync(relay, TimeSpan.FromSeconds(2));
        promptKey!.Value.KeyChar.Should().Be('p');

        relay.EndPrompt();
        source.Writer.TryWrite(new TuiKeyInput(TuiInputAction.InsertText, "e"));
        var engineKey = await ReadWithTimeoutAsync(relay.EngineReader, TimeSpan.FromSeconds(2));
        engineKey!.Value.Text.Should().Be("e");
    }

    [Fact]
    public async Task EndPrompt_ShouldForwardResidualPromptKeysToEngine()
    {
        var source = Channel.CreateUnbounded<TuiKeyInput>();
        var relay = new TuiPromptInputRelay();
        using var cts = new CancellationTokenSource();
        relay.Attach(source.Reader, cts.Token);

        relay.BeginPrompt();
        source.Writer.TryWrite(new TuiKeyInput(TuiInputAction.InsertText, "r"));
        // 留出时间让分发泵把按键写入提示通道（此时按键仍是「未被 Spectre 消费的残留」）。
        await Task.Delay(200, TestContext.Current.CancellationToken);

        relay.EndPrompt();

        var engineKey = await ReadWithTimeoutAsync(relay.EngineReader, TimeSpan.FromSeconds(2));
        engineKey!.Value.Text.Should().Be("r");
    }

    [Fact]
    public async Task PromptMode_ShouldNotDeliverKeysToEngine()
    {
        var source = Channel.CreateUnbounded<TuiKeyInput>();
        var relay = new TuiPromptInputRelay();
        using var cts = new CancellationTokenSource();
        relay.Attach(source.Reader, cts.Token);

        relay.BeginPrompt();
        source.Writer.TryWrite(new TuiKeyInput(TuiInputAction.InsertText, "x"));
        await Task.Delay(200, TestContext.Current.CancellationToken);

        relay.EngineReader.TryRead(out _).Should().BeFalse();

        var promptKey = await ReadPromptWithTimeoutAsync(relay, TimeSpan.FromSeconds(2));
        promptKey!.Value.KeyChar.Should().Be('x');
        relay.EndPrompt();
    }

    [Fact]
    public async Task NestedPrompts_ShouldRemainInPromptModeUntilOutermostEnd()
    {
        var source = Channel.CreateUnbounded<TuiKeyInput>();
        var relay = new TuiPromptInputRelay();
        using var cts = new CancellationTokenSource();
        relay.Attach(source.Reader, cts.Token);

        relay.BeginPrompt();
        relay.BeginPrompt();
        relay.EndPrompt();

        source.Writer.TryWrite(new TuiKeyInput(TuiInputAction.InsertText, "n"));
        var innerKey = await ReadPromptWithTimeoutAsync(relay, TimeSpan.FromSeconds(2));
        innerKey!.Value.KeyChar.Should().Be('n');

        relay.EndPrompt();
        source.Writer.TryWrite(new TuiKeyInput(TuiInputAction.InsertText, "m"));
        var engineKey = await ReadWithTimeoutAsync(relay.EngineReader, TimeSpan.FromSeconds(2));
        engineKey!.Value.Text.Should().Be("m");
    }

    private static async Task<ConsoleKeyInfo?> ReadPromptWithTimeoutAsync(TuiPromptInputRelay relay, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            return await relay.Input.ReadKeyAsync(true, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
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
}
