using System.Diagnostics;
using System.Threading.Channels;
using FluentAssertions;
using Moq;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Tui.Services;
using Seeing.Session.Core;

namespace Seeing.Agent.Tui.Tests.Services;

/// <summary>
/// <see cref="TuiTaskTracker"/> 的发布语义：已发布 <see cref="TuiToolState"/> 换新对象而非原地改集合；
/// 已终态 task 不再重建 pump / 重新订阅。
/// </summary>
public sealed class TuiTaskTrackerSnapshotTests
{
    [Fact]
    public async Task ApplyStep_ShouldPublishNewToolObject_AndNotMutatePublishedSteps()
    {
        var tool = NewTool("call-1", "task", taskId: "child-1");
        var state = NewState("parent", tool);
        var pumps = new PumpRegistry();
        await using var tracker = new TuiTaskTracker(state, pumps.Factory, Groups());

        tracker.Observe(tool);
        await WaitUntilAsync(() => pumps.Count == 1);

        pumps.Last!.Emit(ToolCall("child-1", "c1", "read", ToolCallStatus.Success));
        await WaitUntilAsync(() => CurrentTool(state, "call-1").Steps.Count == 1);
        var firstPublished = CurrentTool(state, "call-1");
        var firstSteps = firstPublished.Steps;

        pumps.Last!.Emit(ToolCall("child-1", "c2", "bash", ToolCallStatus.Success));
        await WaitUntilAsync(() => CurrentTool(state, "call-1").Steps.Count == 2);
        var secondPublished = CurrentTool(state, "call-1");

        secondPublished.Should().NotBeSameAs(firstPublished);
        firstSteps.Should().BeSameAs(firstPublished.Steps);
        firstSteps.Should().ContainSingle();
        secondPublished.Steps.Should().HaveCount(2);
    }

    [Fact]
    public async Task TerminalMount_ShouldNotBeRebuiltByReconcileOrObserve()
    {
        var tool = NewTool("call-1", "task", taskId: "child-1");
        var state = NewState("parent", tool);
        var pumps = new PumpRegistry();
        await using var tracker = new TuiTaskTracker(state, pumps.Factory, Groups());

        tracker.Observe(tool);
        await WaitUntilAsync(() => pumps.Count == 1);
        pumps.Last!.Emit(new ExecutionCompleteEvent { SessionId = "child-1" });
        await WaitUntilAsync(() => pumps.Last!.Stopped);

        await tracker.ReconcileAsync();
        tracker.Observe(tool);
        await Task.Delay(80);

        pumps.Count.Should().Be(1);
    }

    [Fact]
    public async Task TerminalTask_NewCallBoundToSameTask_ShouldNotMountAgain()
    {
        var toolA = NewTool("call-a", "task", taskId: "child-1");
        var state = NewState("parent", toolA);
        var pumps = new PumpRegistry();
        await using var tracker = new TuiTaskTracker(state, pumps.Factory, Groups());

        tracker.Observe(toolA);
        await WaitUntilAsync(() => pumps.Count == 1);
        pumps.Last!.Emit(new ExecutionCompleteEvent { SessionId = "child-1" });
        await WaitUntilAsync(() => pumps.Last!.Stopped);

        var toolB = NewTool("call-b", "task", taskId: "child-1");
        state.Upsert(new TuiBlock
        {
            Key = "tool:call-b",
            Kind = TuiBlockKind.Tool,
            Tool = toolB,
        });

        tracker.Observe(toolB);
        await Task.Delay(80);

        pumps.Count.Should().Be(1);
    }

    private static TuiToolState CurrentTool(TuiViewState state, string callId)
        => state.Find($"tool:{callId}")!.Tool!;

    private static TuiViewState NewState(string sessionId, params TuiToolState[] tools)
    {
        var state = new TuiViewState { SessionId = sessionId };
        foreach (var tool in tools)
        {
            state.Upsert(new TuiBlock
            {
                Key = $"tool:{tool.CallId}",
                Kind = TuiBlockKind.Tool,
                Tool = tool,
            });
        }

        return state;
    }

    private static TuiToolState NewTool(string callId, string name, string? taskId = null)
        => new() { CallId = callId, Name = name, TaskId = taskId };

    private static ISessionGroupManager Groups()
    {
        var mock = new Mock<ISessionGroupManager>();
        mock.Setup(g => g.ListChildrenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SessionData>());
        return mock.Object;
    }

    private static ToolCallEvent ToolCall(
        string sessionId,
        string callId,
        string name,
        ToolCallStatus status)
        => new()
        {
            SessionId = sessionId,
            Type = MessageEventType.ToolCallComplete,
            ToolCallId = callId,
            ToolName = name,
            Status = status,
        };

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException("等待条件超时");
            await Task.Delay(10);
        }
    }

    private sealed class PumpRegistry
    {
        private readonly object _lock = new();
        private readonly List<FakeEventPump> _pumps = [];

        public Func<string, ITuiEventPump> Factory => Create;

        public int Count
        {
            get
            {
                lock (_lock)
                    return _pumps.Count;
            }
        }

        public FakeEventPump? Last
        {
            get
            {
                lock (_lock)
                    return _pumps.Count == 0 ? null : _pumps[^1];
            }
        }

        private ITuiEventPump Create(string sessionId)
        {
            var pump = new FakeEventPump(sessionId);
            lock (_lock)
                _pumps.Add(pump);
            return pump;
        }
    }

    private sealed class FakeEventPump : ITuiEventPump
    {
        private readonly Channel<IMessageEvent> _channel = Channel.CreateUnbounded<IMessageEvent>();

        public FakeEventPump(string sessionId) => SessionId = sessionId;

        public string SessionId { get; }

        public ChannelReader<IMessageEvent> Reader => _channel.Reader;

        public bool Stopped { get; private set; }

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        public Task StopAsync()
        {
            Stopped = true;
            _channel.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        public void Emit(IMessageEvent evt) => _channel.Writer.TryWrite(evt);
    }
}
