using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.App;
using Seeing.Agent.Execution;
using Seeing.Agent.WebUI.Services;
using Seeing.Session.Core;

namespace Seeing.Agent.WebUI.Tests.Services;

public class ConferenceRegistryTests
{
    private static SessionData CreateChild(string childId, string parentId, string originToolCallId)
    {
        var child = SessionData.Create("p1", "explore");
        child.Id = childId;
        child.Kind = SessionKind.SubAgent;
        child.ParentSessionId = parentId;
        child.Metadata[SessionMetadataKeys.OriginToolCallId] = originToolCallId;
        return child;
    }

    private static SessionEventStreamRouter CreateRouter(Mock<IChatOrchestrator> orchestrator)
        => new(orchestrator.Object, Mock.Of<IServiceScopeFactory>(), NullLogger<SessionEventStreamRouter>.Instance);

    private static Mock<IChatOrchestrator> CreateOrchestratorMock(
        Dictionary<string, Channel<IMessageEvent>> channels)
    {
        var orchestrator = new Mock<IChatOrchestrator>();
        orchestrator.Setup(o => o.GetBufferedEvents(It.IsAny<string>())).Returns(new List<IMessageEvent>());
        orchestrator.Setup(o => o.SubscribeEvents(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string sessionId, CancellationToken _) => channels[sessionId].Reader.ReadAllAsync());
        return orchestrator;
    }

    [Fact]
    public async Task Rebind_ShouldEnumerateChildrenIntoWindows()
    {
        var parentId = "parent1";
        var child = CreateChild("child1", parentId, "call-1");
        var parentChannel = Channel.CreateUnbounded<IMessageEvent>();
        var sm = new Mock<ISessionManager>();
        sm.Setup(m => m.ListChildrenAsync(parentId, SessionKind.SubAgent, It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<IReadOnlyList<SessionData>>(new[] { child }));
        var orchestrator = CreateOrchestratorMock(new Dictionary<string, Channel<IMessageEvent>> { [parentId] = parentChannel });

        using var router = CreateRouter(orchestrator);
        var registry = new ConferenceRegistry(router, sm.Object,
            new TaskSessionResolver(sm.Object));
        registry.Rebind(parentId);
        await Task.Delay(200);

        registry.Windows.Should().ContainSingle(w => w.SessionId == "child1");
    }

    [Fact]
    public async Task OnEvent_TaskToolCall_ShouldAddNewWindowAndRaiseChanged()
    {
        var parentId = "parent1";
        var child = CreateChild("child1", parentId, "call-1");
        var parentChannel = Channel.CreateUnbounded<IMessageEvent>();
        var sm = new Mock<ISessionManager>();
        // 初始枚举返回空（模拟子会话尚未在缓存/磁盘出现），验证"动态识别"路径独立生效
        sm.Setup(m => m.ListChildrenAsync(parentId, SessionKind.SubAgent, It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<IReadOnlyList<SessionData>>(Array.Empty<SessionData>()));
        sm.Setup(m => m.LoadChildrenFromStorageAsync(parentId, It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<IReadOnlyList<SessionData>>(Array.Empty<SessionData>()));
        sm.SetupSequence(m => m.ListChildrenAsync(parentId, SessionKind.SubAgent, It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<IReadOnlyList<SessionData>>(Array.Empty<SessionData>()))
            .Returns(Task.FromResult<IReadOnlyList<SessionData>>(new[] { child }));
        var orchestrator = CreateOrchestratorMock(new Dictionary<string, Channel<IMessageEvent>> { [parentId] = parentChannel });

        using var router = CreateRouter(orchestrator);
        var registry = new ConferenceRegistry(router, sm.Object,
            new TaskSessionResolver(sm.Object));
        var changed = 0;
        registry.WindowsChanged += () => changed++;
        registry.Rebind(parentId);

        await parentChannel.Writer.WriteAsync(new ToolCallEvent
        {
            SessionId = parentId, Type = MessageEventType.ToolCallRunning,
            ToolCallId = "call-1", ToolName = "task", Status = ToolCallStatus.Running
        });
        await Task.Delay(300);

        changed.Should().BeGreaterThan(0);
        registry.Windows.Should().Contain(w => w.SessionId == "child1");
    }

    [Fact]
    public async Task OnEvent_DuplicateTaskCall_ShouldNotAddDuplicateWindow()
    {
        var parentId = "parent1";
        var child = CreateChild("child1", parentId, "call-1");
        var parentChannel = Channel.CreateUnbounded<IMessageEvent>();
        var sm = new Mock<ISessionManager>();
        sm.Setup(m => m.ListChildrenAsync(parentId, SessionKind.SubAgent, It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<IReadOnlyList<SessionData>>(new[] { child }));
        sm.Setup(m => m.LoadChildrenFromStorageAsync(parentId, It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<IReadOnlyList<SessionData>>(Array.Empty<SessionData>()));
        var orchestrator = CreateOrchestratorMock(new Dictionary<string, Channel<IMessageEvent>> { [parentId] = parentChannel });

        using var router = CreateRouter(orchestrator);
        var registry = new ConferenceRegistry(router, sm.Object,
            new TaskSessionResolver(sm.Object));
        registry.Rebind(parentId);
        await Task.Delay(200);
        registry.Windows.Should().ContainSingle(w => w.SessionId == "child1");

        await parentChannel.Writer.WriteAsync(new ToolCallEvent
        {
            SessionId = parentId, Type = MessageEventType.ToolCallRunning,
            ToolCallId = "call-1", ToolName = "task", Status = ToolCallStatus.Running
        });
        await Task.Delay(200);

        registry.Windows.Count(w => w.SessionId == "child1").Should().Be(1);
    }

    [Fact]
    public async Task Rebind_DiskFallback_ShouldEnumerateFromStorageWhenMemoryEmpty()
    {
        var parentId = "parent1";
        var child = CreateChild("child1", parentId, "call-1");
        var parentChannel = Channel.CreateUnbounded<IMessageEvent>();
        var sm = new Mock<ISessionManager>();
        sm.Setup(m => m.ListChildrenAsync(parentId, SessionKind.SubAgent, It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<IReadOnlyList<SessionData>>(Array.Empty<SessionData>()));
        sm.Setup(m => m.LoadChildrenFromStorageAsync(parentId, It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<IReadOnlyList<SessionData>>(new[] { child }));
        var orchestrator = CreateOrchestratorMock(new Dictionary<string, Channel<IMessageEvent>> { [parentId] = parentChannel });

        using var router = CreateRouter(orchestrator);
        var registry = new ConferenceRegistry(router, sm.Object,
            new TaskSessionResolver(sm.Object));
        registry.Rebind(parentId);
        await Task.Delay(200);

        registry.Windows.Should().ContainSingle(w => w.SessionId == "child1");
    }

    [Fact]
    public async Task CompletionEvent_ShouldNotRemoveWindow()
    {
        var parentId = "parent1";
        var child = CreateChild("child1", parentId, "call-1");
        var parentChannel = Channel.CreateUnbounded<IMessageEvent>();
        var sm = new Mock<ISessionManager>();
        sm.Setup(m => m.ListChildrenAsync(parentId, SessionKind.SubAgent, It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<IReadOnlyList<SessionData>>(new[] { child }));
        sm.Setup(m => m.LoadChildrenFromStorageAsync(parentId, It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<IReadOnlyList<SessionData>>(Array.Empty<SessionData>()));
        var orchestrator = CreateOrchestratorMock(new Dictionary<string, Channel<IMessageEvent>> { [parentId] = parentChannel });

        using var router = CreateRouter(orchestrator);
        var registry = new ConferenceRegistry(router, sm.Object,
            new TaskSessionResolver(sm.Object));
        registry.Rebind(parentId);
        await Task.Delay(200);
        registry.Windows.Should().ContainSingle(w => w.SessionId == "child1");

        await parentChannel.Writer.WriteAsync(new ExecutionCompleteEvent
        {
            SessionId = parentId, ExecutionId = "e1",
            Status = Seeing.Agent.Execution.ExecutionStatus.Completed
        });
        await Task.Delay(200);

        registry.Windows.Should().ContainSingle(w => w.SessionId == "child1");
    }

    [Fact]
    public async Task RemoveWindows_ShouldRemoveMatchingAndRaiseChanged()
    {
        var parentId = "parent1";
        var child1 = CreateChild("child1", parentId, "call-1");
        var child2 = CreateChild("child2", parentId, "call-2");
        var parentChannel = Channel.CreateUnbounded<IMessageEvent>();
        var sm = new Mock<ISessionManager>();
        sm.Setup(m => m.ListChildrenAsync(parentId, SessionKind.SubAgent, It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<IReadOnlyList<SessionData>>(new[] { child1, child2 }));
        sm.Setup(m => m.LoadChildrenFromStorageAsync(parentId, It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<IReadOnlyList<SessionData>>(Array.Empty<SessionData>()));
        var orchestrator = CreateOrchestratorMock(new Dictionary<string, Channel<IMessageEvent>> { [parentId] = parentChannel });

        using var router = CreateRouter(orchestrator);
        var registry = new ConferenceRegistry(router, sm.Object,
            new TaskSessionResolver(sm.Object));
        var changed = 0;
        registry.WindowsChanged += () => changed++;
        registry.Rebind(parentId);
        await Task.Delay(200);
        registry.Windows.Should().HaveCount(2);

        registry.RemoveWindows(new[] { "child1" });

        registry.Windows.Should().ContainSingle(w => w.SessionId == "child2");
        registry.Windows.Should().NotContain(w => w.SessionId == "child1");
        changed.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task RemoveWindows_UnknownIds_ShouldNotRaiseChanged()
    {
        var parentId = "parent1";
        var child = CreateChild("child1", parentId, "call-1");
        var parentChannel = Channel.CreateUnbounded<IMessageEvent>();
        var sm = new Mock<ISessionManager>();
        sm.Setup(m => m.ListChildrenAsync(parentId, SessionKind.SubAgent, It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<IReadOnlyList<SessionData>>(new[] { child }));
        sm.Setup(m => m.LoadChildrenFromStorageAsync(parentId, It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<IReadOnlyList<SessionData>>(Array.Empty<SessionData>()));
        var orchestrator = CreateOrchestratorMock(new Dictionary<string, Channel<IMessageEvent>> { [parentId] = parentChannel });

        using var router = CreateRouter(orchestrator);
        var registry = new ConferenceRegistry(router, sm.Object,
            new TaskSessionResolver(sm.Object));
        var changed = 0;
        registry.WindowsChanged += () => changed++;
        registry.Rebind(parentId);
        await Task.Delay(200);
        registry.Windows.Should().ContainSingle(w => w.SessionId == "child1");
        var countAfterRebind = changed;

        registry.RemoveWindows(new[] { "nonexistent" });

        registry.Windows.Should().ContainSingle(w => w.SessionId == "child1");
        changed.Should().Be(countAfterRebind);
    }

    [Fact]
    public async Task Rebind_DifferentParent_ShouldClearOldAndEnumerateNew()
    {
        var parentA = "parentA";
        var parentB = "parentB";
        var childA = CreateChild("childA", parentA, "call-a");
        var childB = CreateChild("childB", parentB, "call-b");
        var channelA = Channel.CreateUnbounded<IMessageEvent>();
        var channelB = Channel.CreateUnbounded<IMessageEvent>();
        var sm = new Mock<ISessionManager>();
        sm.Setup(m => m.ListChildrenAsync(parentA, SessionKind.SubAgent, It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<IReadOnlyList<SessionData>>(new[] { childA }));
        sm.Setup(m => m.LoadChildrenFromStorageAsync(parentA, It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<IReadOnlyList<SessionData>>(Array.Empty<SessionData>()));
        sm.Setup(m => m.ListChildrenAsync(parentB, SessionKind.SubAgent, It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<IReadOnlyList<SessionData>>(new[] { childB }));
        sm.Setup(m => m.LoadChildrenFromStorageAsync(parentB, It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<IReadOnlyList<SessionData>>(Array.Empty<SessionData>()));
        var orchestrator = CreateOrchestratorMock(new Dictionary<string, Channel<IMessageEvent>>
        {
            [parentA] = channelA, [parentB] = channelB
        });

        using var router = CreateRouter(orchestrator);
        var registry = new ConferenceRegistry(router, sm.Object,
            new TaskSessionResolver(sm.Object));

        var changeCount = 0;
        registry.WindowsChanged += () => changeCount++;

        registry.Rebind(parentA);
        await Task.Delay(200);
        registry.Windows.Should().ContainSingle(w => w.SessionId == "childA");
        var countAfterA = changeCount;

        registry.Rebind(parentB);
        await Task.Delay(200);
        registry.Windows.Should().ContainSingle(w => w.SessionId == "childB");
        registry.Windows.Should().NotContain(w => w.SessionId == "childA");
        changeCount.Should().BeGreaterThan(countAfterA);
    }
}
