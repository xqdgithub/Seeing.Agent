using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PuppeteerSharp;
using Xunit;

namespace Seeing.Agent.Tools.Browser.Tests;

/// <summary>
/// 画面流管线：帧泵的 ack 恰好一次、静帧源的尺寸与开关。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么 ack 值得单独测</b>：Chromium 收到 <c>Page.screencastFrameAck</c> 才推下一帧。
/// 漏一次的表现不是「掉一帧」，而是<b>画面永久停在最后一帧</b>；多发一次会让帧窗口计数错乱。
/// 所以「恰好一次」是契约，不是实现细节。
/// </para>
/// <para>
/// 本文件只用 <see cref="FramePump.DrainAsync"/> 这种完成信号同步，不使用固定等待。
/// </para>
/// </remarks>
public class BrowserScreencastPipelineTests
{
    [Fact]
    public async Task FramePump_EveryFrame_ShouldAckExactlyOnce()
    {
        var acks = 0;
        var pump = FramePump.CreateForTest(_ => { });
        pump.Offer(new BrowserFrame([1], 1, 1, 1), () => Interlocked.Increment(ref acks));
        pump.Offer(new BrowserFrame([2], 2, 1, 1), () => Interlocked.Increment(ref acks));
        await pump.DrainAsync();
        acks.Should().Be(2);
    }

    [Fact]
    public async Task FramePump_WhenBroadcastThrows_ShouldStillAck()
    {
        var acks = 0;
        var pump = FramePump.CreateForTest(_ => throw new InvalidOperationException());
        pump.Offer(new BrowserFrame([1], 1, 1, 1), () => Interlocked.Increment(ref acks));
        await pump.DrainAsync();
        acks.Should().Be(1);
    }

    [Fact]
    public async Task FramePump_WhenPinnedInFlush_ShouldAckReplacedFrameExactlyOnce()
    {
        // 目标路径（旧实现在这里必红）：泵正卡在广播 A 帧（_flushing == true），
        // 期间 B 帧入列、又被 C 帧顶掉。B 从未被广播，但它的 ack 必须恰好来一次——
        // 否则那一帧的 Chromium 窗口不释放，后续帧不再到达（画面永久停在最后一帧）。
        //
        // 不用固定等待：用 TCS 把泵钉在 flush 里，场景边界完全由测试控制。
        var broadcastEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBroadcast = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var pump = FramePump.CreateForTest(_ =>
        {
            broadcastEntered.TrySetResult();

            // 测试桩：把泵钉在「正在广播」的状态（这是被测场景本身，不是等待技巧）
            releaseBroadcast.Task.GetAwaiter().GetResult();
        });

        var acks = new int[3];

        pump.Offer(new BrowserFrame([1], 1, 1, 1), () => Interlocked.Increment(ref acks[0]));
        await broadcastEntered.Task;        // A 帧已进入广播 → 泵处于 flushing

        pump.Offer(new BrowserFrame([2], 2, 1, 1), () =>
        {
            // B 帧的 ack 会抛（CDP 已断的现场）：仍必须被调用一次，
            // 且不能把泵打断，否则 C 帧永远发不出去。
            Interlocked.Increment(ref acks[1]);
            throw new InvalidOperationException();
        });

        pump.Offer(new BrowserFrame([3], 3, 1, 1), () => Interlocked.Increment(ref acks[2]));

        releaseBroadcast.TrySetResult();
        await pump.DrainAsync();

        acks[0].Should().Be(1, "广播中的帧必须在广播后 ack");
        acks[1].Should().Be(1, "被覆盖的帧必须 ack，且只一次（即使 ack 自身抛异常）");
        acks[2].Should().Be(1, "顶替后的帧必须 ack");
    }

    [Fact]
    public async Task FramePump_WhenNoFrameOffered_ShouldDrainImmediately()
    {
        var pump = FramePump.CreateForTest(_ => { });

        await pump.DrainAsync();
    }

    [Fact]
    public async Task RestartingStream_ShouldIncreaseEpochAndKeepStateAndFrameHeaderInSync()
    {
        // 验收 #15：「流重建（epoch）后画面继续更新」。
        // 只断言「epoch 变了」不够——前端同时拿 state 与帧头做仲裁；
        // 两者若不同源，重启后要么新帧被当成旧帧丢掉，要么画布按错误的代次重置。
        var options = new BrowserOptions();
        var optionsMonitor = new TestOptionsMonitor<BrowserOptions>(options);
        var hub = new BrowserScreencastHub(optionsMonitor);

        var session = new Mock<ICDPSession>();
        var page = new Mock<IPage>();
        page.Setup(p => p.CreateCDPSessionAsync()).ReturnsAsync(session.Object);

        var handle = new BrowserPageHandle(
            "session-epoch",
            page.Object,
            refEpoch: 1,
            optionsMonitor,
            NullLogger<BrowserPageHandle>.Instance);

        var sink = new RecordingSink();
        await using var subscription = await hub.SubscribeAsync(
            "session-epoch", handle, sink, TestContext.Current.CancellationToken);

        var firstEpoch = hub.EpochOf("session-epoch");
        firstEpoch.Should().BeGreaterThan(0, "订阅成功后必须已有代次，前端不能拿到 0");

        sink.LastState!.Epoch.Should().Be(firstEpoch, "订阅时推的 state 必须与当前代次一致");

        RaiseScreencastFrame(session, "AA==");
        var firstFrame = await sink.WaitNextFrameAsync(TimeSpan.FromSeconds(5));
        firstFrame.Epoch.Should().Be(firstEpoch, "帧头必须与 state 同源");
        firstFrame.Seq.Should().Be(1);

        // 重建流（面板 resize / 页面重建走的是同一条路径）。
        await hub.RestartAsync("session-epoch", TestContext.Current.CancellationToken);

        var secondEpoch = hub.EpochOf("session-epoch");
        secondEpoch.Should().BeGreaterThan(firstEpoch, "重建后代次必须严格变大，否则前端会继续按旧代次丢弃新帧");

        // 推一次状态（端点在真实链路上每个发布点都会走 CreateState）。
        hub.PublishState("session-epoch", handle);
        sink.LastState!.Epoch.Should().Be(secondEpoch, "重建后的 state 必须报告新代次");

        RaiseScreencastFrame(session, "AQ==");
        var secondFrame = await sink.WaitNextFrameAsync(TimeSpan.FromSeconds(5));

        // CDP 的帧序号在重启后会从头开始：靠 seq 已经无法区分新旧，
        // 前端唯一能靠的就是代次 —— 因此帧头必须带新代次。
        secondFrame.Epoch.Should().Be(secondEpoch, "重建后的帧头必须带新代次");
        secondFrame.Epoch.Should().Be(sink.LastState!.Epoch, "state 与帧头必须逐字段一致");
    }

    [Fact]
    public async Task StartScreencast_ShouldEncodeAtViewportTimesFrameScale()
    {
        // 单源架构的核心契约：编码尺寸 = 视口 × 帧倍率（默认 dpr）。
        // 只给 CSS 视口尺寸会让 Chromium 把物理像素表面降采样后再编码，
        // 前端画布只能放大显示 → 文字发虚 → 又诱使「再抓一张静帧」的第二来源。
        var options = new BrowserOptions();
        var monitor = new TestOptionsMonitor<BrowserOptions>(options);
        var hub = new BrowserScreencastHub(monitor);

        var cdp = new Mock<ICDPSession>();
        Dictionary<string, object>? startArgs = null;
        cdp.Setup(s => s.SendAsync(
                "Page.startScreencast", It.IsAny<object?>(), It.IsAny<bool>(), It.IsAny<CommandOptions?>()))
            .Callback<string, object?, bool, CommandOptions?>((_, payload, __, ___) =>
                startArgs = payload as Dictionary<string, object>)
            .ReturnsAsync((JsonElement?)null);

        var page = new Mock<IPage>();
        page.Setup(p => p.CreateCDPSessionAsync()).ReturnsAsync(cdp.Object);
        page.Setup(p => p.GetTitleAsync()).ReturnsAsync("title");

        var handle = new BrowserPageHandle(
            "session-scale",
            page.Object,
            refEpoch: 1,
            monitor,
            NullLogger<BrowserPageHandle>.Instance);

        // 面板 800×600、dpr 1.25 → 视口 800×600、倍率 1.25 → 编码 1000×750
        await handle.ResizeAsync(800, 600, 1.25, TestContext.Current.CancellationToken);

        var sink = new RecordingSink();
        await using var subscription = await hub.SubscribeAsync(
            "session-scale", handle, sink, TestContext.Current.CancellationToken);

        startArgs.Should().NotBeNull();
        startArgs!["maxWidth"].Should().Be(1000);
        startArgs["maxHeight"].Should().Be(750);

        sink.LastState!.ViewportWidth.Should().Be(800, "排版视口仍是面板 CSS 尺寸");
        sink.LastState.FrameScale.Should().Be(1.25, "帧倍率与编码尺寸同源");
    }

    private static void RaiseScreencastFrame(Mock<ICDPSession> session, string base64)
    {
        var json = JsonSerializer.Serialize(new
        {
            sessionId = 1,
            data = base64,
            metadata = new { deviceWidth = 100, deviceHeight = 50 },
        });

        using var document = JsonDocument.Parse(json);

        // MessageEventArgs 的 setter 是 internal（PuppeteerSharp 不公开构造入口），
        // 这里用反射设置：只在测试里模拟 CDP 推送，生产代码不依赖它。
        var args = new MessageEventArgs();
        SetProperty(args, nameof(MessageEventArgs.MessageID), "Page.screencastFrame");
        SetProperty(args, nameof(MessageEventArgs.MessageData), document.RootElement.Clone());

        session.Raise(s => s.MessageReceived += null, args);
    }

    private static void SetProperty(object target, string name, object value)
    {
        var property = target.GetType().GetProperty(name)
                       ?? throw new InvalidOperationException($"PuppeteerSharp 的 {name} 属性不存在");

        var setter = property.GetSetMethod(nonPublic: true)
                     ?? throw new InvalidOperationException($"PuppeteerSharp 的 {name} 不可写");

        setter.Invoke(target, [value]);
    }

    /// <summary>记录状态与帧的观看者；帧用完成信号通知，不做固定等待。</summary>
    /// <remarks>
    /// <b>必须先查积压队列再等</b>：帧泵在 <c>Task.Run</c> 上广播，第一帧可能在测试调用
    /// <see cref="WaitNextFrameAsync"/> 之前就到。若实现成「每次等待换一个新 TCS」，
    /// 已经到达的帧会被丢掉，表现为「明明在推帧却超时」。
    /// </remarks>
    private sealed class RecordingSink : IBrowserFrameSink
    {
        private readonly object _gate = new();
        private readonly Queue<BrowserFrame> _frames = new();
        private TaskCompletionSource<BrowserFrame>? _waiter;

        public BrowserPageState? LastState { get; private set; }

        public void OnFrame(BrowserFrame frame)
        {
            TaskCompletionSource<BrowserFrame>? waiter;
            lock (_gate)
            {
                waiter = _waiter;
                _waiter = null;

                if (waiter is null)
                    _frames.Enqueue(frame);
            }

            waiter?.TrySetResult(frame);
        }

        public void OnState(BrowserPageState state) => LastState = state;

        public void OnError(string message)
        {
        }

        public async Task<BrowserFrame> WaitNextFrameAsync(TimeSpan timeout)
        {
            Task<BrowserFrame> pending;
            lock (_gate)
            {
                if (_frames.Count > 0)
                    return _frames.Dequeue();

                _waiter ??= new TaskCompletionSource<BrowserFrame>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                pending = _waiter.Task;
            }

            return await pending.WaitAsync(timeout).ConfigureAwait(false);
        }
    }

    private static JsonElement? Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();    // Clone 后即使文档释放仍可安全读取
    }
}
