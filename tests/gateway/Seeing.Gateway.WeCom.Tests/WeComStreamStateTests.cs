using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Seeing.Gateway.WeCom.Connection;
using Xunit;

namespace Seeing.Gateway.WeCom.Tests;

/// <summary>
/// 企微流式回复状态机测试。
/// <para><b>时间策略</b>：所有涉及 keepalive / 节流 / 超时的用例一律注入
/// <see cref="FakeTimeProvider"/> 并手动 <c>Advance</c>，<b>禁止 <c>Task.Delay</c> 等待真实时长</b>。
/// 这样用例既确定（不受机器负载影响），又能在毫秒级完成（原先每个用例要等 6 秒）。</para>
/// <para>等待异步副作用收敛用 <c>WaitUntilAsync</c>（短间隔轮询 + 超时），
/// 它只等「状态变化发生」，不等「固定的墙钟时长」。</para>
/// </summary>
public class WeComStreamStateTests
{
    private const string ProcessingText = "🤔 Thinking...";

    /// <summary>
    /// 推送到目标状态达成，最多等 <paramref name="timeoutMs"/>；超时即断言失败。
    /// <para>这是「等事件」而非「等时长」：条件一满足立刻返回，通常 1–20ms。</para>
    /// </summary>
    private static async Task WaitUntilAsync(
        Func<bool> condition,
        string because,
        int timeoutMs = 2000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
                return;
            await Task.Yield();
            await Task.Delay(1, TestContext.Current.CancellationToken).ConfigureAwait(false);
        }

        condition().Should().BeTrue(because);
    }

    /// <summary>构建带可控时钟的状态机。</summary>
    private static (WeComStreamState State, RecordingStreamSender Sender, FakeTimeProvider Clock) Build(
        WeComOptions options)
    {
        var sender = new RecordingStreamSender();
        var clock = new FakeTimeProvider();
        var state = new WeComStreamState(
            sender,
            client: null,
            new WeComWsFrame(),
            options,
            registry: null,
            timeProvider: clock);
        return (state, sender, clock);
    }

    [Fact]
    public async Task PublishAsync_ShouldStopKeepaliveBeforeRefresh()
    {
        var options = new WeComOptions
        {
            StreamingEnabled = true,
            ProcessingRefreshSeconds = 5
        };
        var (state, sender, clock) = Build(options);

        await using var _ = state;
        await state.BeginAsync(CancellationToken.None);
        await state.PublishAsync("hello", CancellationToken.None);

        // 推进超过 keepalive 周期：若 keepalive 未停止，会再发一条 ProcessingText
        await WaitUntilAsync(
            () => sender.Records.Any(r => r.Content == "hello"),
            "正文必须先发出");
        clock.Advance(TimeSpan.FromSeconds(12));

        var helloIndex = sender.Records.FindIndex(r => r.Content == "hello");
        helloIndex.Should().BeGreaterThanOrEqualTo(0);
        sender.Records
            .Skip(helloIndex + 1)
            .Any(r => r.Content == ProcessingText)
            .Should().BeFalse("keepalive must stop once content is published");
    }

    [Fact]
    public async Task PublishAsync_WhenThrottled_ShouldStillStopKeepalive()
    {
        var options = new WeComOptions
        {
            StreamingEnabled = true,
            DeltaThrottleMilliseconds = 5000,
            ProcessingRefreshSeconds = 5
        };
        var (state, sender, clock) = Build(options);

        await using var _ = state;
        await state.BeginAsync(CancellationToken.None);
        await state.PublishAsync("hello", CancellationToken.None);
        await state.PublishAsync("hello world", CancellationToken.None);

        await WaitUntilAsync(
            () => sender.Records.Any(r => r.Content == "hello"),
            "首个增量必须先发出");
        clock.Advance(TimeSpan.FromSeconds(12));

        sender.Records.Count(r => r.Content == "hello world").Should().Be(0,
            "second delta is throttled and should not be sent yet");

        var helloIndex = sender.Records.FindIndex(r => r.Content == "hello");
        sender.Records
            .Any(r => r.Content == ProcessingText && sender.Records.IndexOf(r) > helloIndex)
            .Should().BeFalse("throttled publish must still stop keepalive");
    }

    [Fact]
    public async Task CompleteAsync_AfterContent_ShouldEndWithContentOnSameStream()
    {
        var options = new WeComOptions { StreamingEnabled = true };
        var (state, sender, _) = Build(options);

        await using var __ = state;
        await state.BeginAsync(CancellationToken.None);
        await state.PublishAsync("partial", CancellationToken.None);
        await state.CompleteAsync("final answer", CancellationToken.None);

        sender.Records.Should().NotBeEmpty();
        sender.Records.Select(r => r.StreamId).Distinct().Should().ContainSingle();
        var last = sender.Records[^1];
        last.Content.Should().Be("final answer");
        last.Finish.Should().BeTrue();
    }

    [Fact]
    public async Task Keepalive_ShouldNeverFinishStreamBeforeComplete()
    {
        var options = new WeComOptions
        {
            StreamingEnabled = true,
            ProcessingRefreshSeconds = 5,
            ProcessingMaxDurationSeconds = 5
        };
        var (state, sender, clock) = Build(options);

        await using var _ = state;
        await state.BeginAsync(CancellationToken.None);

        // 推进跨越 max duration：keepalive 会停发，但绝不允许发 finish=true
        clock.Advance(TimeSpan.FromSeconds(12));
        await WaitUntilAsync(
            () => sender.Records.Count > 0,
            "keepalive 至少发出一次占位");

        sender.Records.Should().OnlyContain(r => !r.Finish);
    }

    [Fact]
    public async Task CompleteAsync_AfterKeepaliveElapsed_ShouldReuseSameStreamId()
    {
        var options = new WeComOptions
        {
            StreamingEnabled = true,
            ProcessingRefreshSeconds = 5,
            ProcessingMaxDurationSeconds = 5
        };
        var (state, sender, clock) = Build(options);

        await using var _ = state;
        await state.BeginAsync(CancellationToken.None);

        clock.Advance(TimeSpan.FromSeconds(12));
        await state.CompleteAsync("final answer", CancellationToken.None);

        sender.Records.Select(r => r.StreamId).Distinct().Should().ContainSingle();
        sender.Records[^1].Content.Should().Be("final answer");
        sender.Records[^1].Finish.Should().BeTrue();
    }

    [Fact]
    public async Task SendInstantAsync_ShouldSkipThinkingPlaceholder()
    {
        var options = new WeComOptions();
        var (state, sender, _) = Build(options);

        await using var __ = state;
        await state.SendInstantAsync("✅ 已开启新对话", CancellationToken.None);

        sender.Records.Should().ContainSingle();
        sender.Records[0].Content.Should().Be("✅ 已开启新对话");
        sender.Records[0].Finish.Should().BeTrue();
        sender.Records.Should().NotContain(r => r.Content == ProcessingText);
    }

    [Fact]
    public async Task BeginAsync_WithZeroRefreshConfig_ShouldUseDefaultInterval()
    {
        var options = new WeComOptions
        {
            StreamingEnabled = true,
            ProcessingRefreshSeconds = 0
        };
        var (state, sender, clock) = Build(options);

        await using var _ = state;
        await state.BeginAsync(CancellationToken.None);

        // 零值回落到默认刷新间隔；推进足够长时间确保至少触发一次
        clock.Advance(TimeSpan.FromMinutes(2));
        await WaitUntilAsync(
            () => sender.Records.Any(r => r.Content == ProcessingText),
            "零配置应回落到默认刷新间隔并发出占位");
    }

    [Fact]
    public async Task PublishAsync_WithZeroThrottleConfig_ShouldUseEffectiveDefault()
    {
        var options = new WeComOptions
        {
            StreamingEnabled = true,
            DeltaThrottleMilliseconds = 0
        };
        var (state, sender, clock) = Build(options);

        await using var _ = state;
        await state.BeginAsync(CancellationToken.None);
        await state.PublishAsync("a", CancellationToken.None);

        clock.Advance(TimeSpan.FromMinutes(1));
        await WaitUntilAsync(
            () => sender.Records.Any(r => r.Content == "a"),
            "零节流配置应回落到有效默认值并发出首个增量");
    }

    private sealed class RecordingStreamSender : IWeComStreamSender
    {
        public List<StreamRecord> Records { get; } = [];

        public Task SendProcessingIndicatorAsync(
            WeComWsFrame requestFrame,
            string streamId,
            long connectionEpoch,
            CancellationToken cancellationToken)
        {
            Records.Add(new StreamRecord(streamId, ProcessingText, Finish: false));
            return Task.CompletedTask;
        }

        public Task ReplyStreamAsync(
            WeComWsFrame requestFrame,
            string streamId,
            string content,
            bool finish,
            long connectionEpoch,
            CancellationToken cancellationToken,
            WeComOutboundPriority priority = WeComOutboundPriority.ContentDelta)
        {
            Records.Add(new StreamRecord(streamId, content, finish));
            return Task.CompletedTask;
        }
    }

    private readonly record struct StreamRecord(string StreamId, string Content, bool Finish);
}
