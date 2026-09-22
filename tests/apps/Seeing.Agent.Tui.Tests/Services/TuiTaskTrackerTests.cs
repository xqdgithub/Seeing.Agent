using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Channels;
using System.Threading.Tasks;
using Moq;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Tui.Services;
using Seeing.Session.Core;

namespace Seeing.Agent.Tui.Tests.Services;

public sealed class TuiTaskTrackerTests
{
    [Fact]
    public async Task Observe_TaskToolWithTaskId_ShouldMountChildPump()
    {
        var tool = NewTool("call-1", "task", taskId: "child-1");
        var state = NewState("parent", tool);
        var pumps = new PumpRegistry();
        await using var tracker = new TuiTaskTracker(state, pumps.Factory, Groups(new()));

        tracker.Observe(tool);

        await WaitUntilAsync(() => pumps.Count == 1);
        Assert.Equal("child-1", pumps.Last!.SessionId);
        Assert.True(pumps.Last.Started);
        Assert.Equal("child-1", tool.TaskId);
    }

    [Fact]
    public async Task Observe_TaskToolWithoutTaskId_ShouldResolveByOriginToolCallId()
    {
        var tool = NewTool("call-9", "task");
        var state = NewState("parent", tool);
        var child = NewChild("child-9", "call-9");
        var pumps = new PumpRegistry();
        await using var tracker = new TuiTaskTracker(state, pumps.Factory, Groups(new() { ["parent"] = [child] }));

        tracker.Observe(tool);

        await WaitUntilAsync(() => pumps.Count == 1);
        Assert.Equal("child-9", pumps.Last!.SessionId);
        Assert.Equal("child-9", tool.TaskId);
    }

    [Fact]
    public async Task Observe_TaskToolWithNestedChild_ShouldResolveRecursively()
    {
        var tool = NewTool("call-nested", "task");
        var state = NewState("parent", tool);
        var grandChild = NewChild("grand-child", "call-nested");
        var pumps = new PumpRegistry();
        var tree = new Dictionary<string, IReadOnlyList<SessionData>>
        {
            ["parent"] = [NewChild("child-a", "other-call")],
            ["child-a"] = [grandChild],
        };
        await using var tracker = new TuiTaskTracker(state, pumps.Factory, Groups(tree));

        tracker.Observe(tool);

        await WaitUntilAsync(() => pumps.Count == 1);
        Assert.Equal("grand-child", pumps.Last!.SessionId);
    }

    [Fact]
    public async Task ChildToolCallEvents_ShouldAggregateIntoSingleStep()
    {
        var tool = NewTool("call-1", "task", taskId: "child-1");
        var state = NewState("parent", tool);
        var pumps = new PumpRegistry();
        await using var tracker = new TuiTaskTracker(state, pumps.Factory, Groups(new()));

        tracker.Observe(tool);
        await WaitUntilAsync(() => pumps.Count == 1);
        var pump = pumps.Last!;

        pump.Emit(ToolCall("child-1", "c1", "read", ToolCallStatus.Pending, new Dictionary<string, object> { ["path"] = "/a/b.txt" }));
        pump.Emit(ToolCall("child-1", "c1", "read", ToolCallStatus.Success, new Dictionary<string, object> { ["path"] = "/a/b.txt" }, title: "读取完成"));

        await WaitUntilAsync(() => CurrentTool(state, "call-1").Steps.Count == 1
            && CurrentTool(state, "call-1").Steps[0].Status == TuiToolStatus.Success);
        var step = Assert.Single(CurrentTool(state, "call-1").Steps);
        Assert.Equal("read", step.ToolName);
        Assert.Contains("/a/b.txt", step.Summary);
    }

    [Fact]
    public async Task ChildToolCallWithoutArguments_ShouldFallbackToTitle()
    {
        var tool = NewTool("call-1", "task", taskId: "child-1");
        var state = NewState("parent", tool);
        var pumps = new PumpRegistry();
        await using var tracker = new TuiTaskTracker(state, pumps.Factory, Groups(new()));

        tracker.Observe(tool);
        await WaitUntilAsync(() => pumps.Count == 1);

        pumps.Last!.Emit(ToolCall("child-1", "c1", "bash", ToolCallStatus.Success, arguments: null, title: "执行 ls"));

        await WaitUntilAsync(() => CurrentTool(state, "call-1").Steps.Count == 1);
        Assert.Equal("执行 ls", CurrentTool(state, "call-1").Steps[0].Summary);
    }

    [Fact]
    public async Task ChildExecutionComplete_ShouldStopAndReleasePump()
    {
        var tool = NewTool("call-1", "task", taskId: "child-1");
        var state = NewState("parent", tool);
        var pumps = new PumpRegistry();
        await using var tracker = new TuiTaskTracker(state, pumps.Factory, Groups(new()));

        tracker.Observe(tool);
        await WaitUntilAsync(() => pumps.Count == 1);
        var pump = pumps.Last!;

        pump.Emit(new ExecutionCompleteEvent { SessionId = "child-1" });

        await WaitUntilAsync(() => pump.Stopped);
        var stepCount = CurrentTool(state, "call-1").Steps.Count;
        pump.Emit(ToolCall("child-1", "late", "read", ToolCallStatus.Success, new Dictionary<string, object> { ["path"] = "/late" }));
        await Task.Delay(50);
        Assert.Equal(stepCount, CurrentTool(state, "call-1").Steps.Count);
    }

    [Fact]
    public async Task ChildLoopCancelled_ShouldStopSubscription()
    {
        var tool = NewTool("call-1", "task", taskId: "child-1");
        var state = NewState("parent", tool);
        var pumps = new PumpRegistry();
        await using var tracker = new TuiTaskTracker(state, pumps.Factory, Groups(new()));

        tracker.Observe(tool);
        await WaitUntilAsync(() => pumps.Count == 1);
        var pump = pumps.Last!;

        pump.Emit(new LoopCancelledEvent { SessionId = "child-1", LoopId = "loop-1", Reason = "user" });

        await WaitUntilAsync(() => pump.Stopped);
    }

    [Fact]
    public async Task ChildError_ShouldStopSubscription()
    {
        var tool = NewTool("call-1", "task", taskId: "child-1");
        var state = NewState("parent", tool);
        var pumps = new PumpRegistry();
        await using var tracker = new TuiTaskTracker(state, pumps.Factory, Groups(new()));

        tracker.Observe(tool);
        await WaitUntilAsync(() => pumps.Count == 1);
        var pump = pumps.Last!;

        pump.Emit(new ErrorEvent { SessionId = "child-1", Message = "boom" });

        await WaitUntilAsync(() => pump.Stopped);
    }

    [Fact]
    public async Task RepeatedObserveAndReconcile_ShouldNotDuplicateSubscription()
    {
        var tool = NewTool("call-1", "task", taskId: "child-1");
        var state = NewState("parent", tool);
        var pumps = new PumpRegistry();
        await using var tracker = new TuiTaskTracker(state, pumps.Factory, Groups(new()));

        tracker.Observe(tool);
        await WaitUntilAsync(() => pumps.Count == 1);

        tracker.Observe(tool);
        await tracker.ReconcileAsync();
        tracker.Observe(tool);
        await Task.Delay(50);

        Assert.Equal(1, pumps.Count);
    }

    [Fact]
    public async Task Observe_NonTaskTool_ShouldNotMount()
    {
        var tool = NewTool("call-1", "bash");
        var state = NewState("parent", tool);
        var pumps = new PumpRegistry();
        await using var tracker = new TuiTaskTracker(state, pumps.Factory, Groups(new()));

        tracker.Observe(tool);
        await Task.Delay(50);

        Assert.Equal(0, pumps.Count);
    }

    [Fact]
    public async Task DisposeAsync_ShouldReleaseAllPumps()
    {
        var toolA = NewTool("call-a", "task", taskId: "child-a");
        var toolB = NewTool("call-b", "task", taskId: "child-b");
        var state = NewState("parent", toolA, toolB);
        var pumps = new PumpRegistry();
        var tracker = new TuiTaskTracker(state, pumps.Factory, Groups(new()));

        tracker.Observe(toolA);
        tracker.Observe(toolB);
        await WaitUntilAsync(() => pumps.Count == 2);

        await tracker.DisposeAsync();

        Assert.All(pumps.Snapshot(), p => Assert.True(p.Stopped));
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

    private static SessionData NewChild(string id, string? originCallId)
    {
        var session = SessionData.Create(partitionId: "default");
        session.Id = id;
        session.Title = id;
        if (originCallId is not null)
            session.Metadata[SessionMetadataKeys.OriginToolCallId] = originCallId;
        return session;
    }

    private static ISessionGroupManager Groups(Dictionary<string, IReadOnlyList<SessionData>> tree)
    {
        var mock = new Mock<ISessionGroupManager>();
        mock.Setup(g => g.ListChildrenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string parent, CancellationToken _) =>
                tree.TryGetValue(parent, out var children) ? children : Array.Empty<SessionData>());
        return mock.Object;
    }

    private static ToolCallEvent ToolCall(
        string sessionId,
        string callId,
        string name,
        ToolCallStatus status,
        object? arguments = null,
        string? title = null)
    {
        var type = status switch
        {
            ToolCallStatus.Pending => MessageEventType.ToolCallPending,
            ToolCallStatus.Running => MessageEventType.ToolCallRunning,
            _ => MessageEventType.ToolCallComplete,
        };

        return new ToolCallEvent
        {
            SessionId = sessionId,
            Type = type,
            ToolCallId = callId,
            ToolName = name,
            Arguments = arguments,
            Status = status,
            Title = title,
        };
    }

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

        public IReadOnlyList<FakeEventPump> Snapshot()
        {
            lock (_lock)
                return _pumps.ToArray();
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

        public bool Started { get; private set; }

        public bool Stopped { get; private set; }

        public Task StartAsync(CancellationToken ct)
        {
            Started = true;
            return Task.CompletedTask;
        }

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
