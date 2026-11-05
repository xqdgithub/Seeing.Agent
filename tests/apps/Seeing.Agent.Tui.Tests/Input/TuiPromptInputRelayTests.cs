using FluentAssertions;
using Seeing.Agent.Tui.Input;
using System.Threading.Channels;

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

        // 无法用可观察状态替代：提示通道由 Spectre 消费，测试里无消费者，
        // IsKeyAvailable() 也不反映分发泵是否已写入（实测不成立）。
        // 这里等的是真实 IO 泵的一次写入，属 AGENTS.md 允许的例外——
        // 但已从 200ms 固定值改为带上限的短轮询，命中即返回。
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

        // 先确认按键已进提示通道（确定性），再断言没有进引擎通道。
        var promptKey = await ReadPromptWithTimeoutAsync(relay, TimeSpan.FromSeconds(5));
        relay.EngineReader.TryRead(out _).Should().BeFalse();
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

    /// <summary>条件轮询：命中即返回，上限 5s。用于等待可观察状态，不用于「等它稳定」。</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("等待条件超时");

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private static async Task<ConsoleKeyInfo?> ReadPromptWithTimeoutAsync(TuiPromptInputRelay relay, TimeSpan timeout)    {
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
