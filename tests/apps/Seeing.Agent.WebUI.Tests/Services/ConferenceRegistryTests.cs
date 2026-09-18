using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Chat;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Core.Execution;
using Seeing.Agent.WebUI.Services;
using Seeing.Session.Core;

namespace Seeing.Agent.WebUI.Tests.Services;

public class ConferenceRegistryTests
{
    private static SessionData CreateChild(string childId, string originToolCallId)
    {
        var child = SessionData.Create("p1", "explore");
        child.Id = childId;
        child.Kind = SessionKind.SubAgent;
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

    private static Mock<ISessionGroupManager> CreateGroupManager(
        Dictionary<string, SessionData[]> childrenByParent)
    {
        var gm = new Mock<ISessionGroupManager>();
        gm.Setup(m => m.ListChildrenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string parentId, CancellationToken _) =>
                childrenByParent.TryGetValue(parentId, out var children)
                    ? (IReadOnlyList<SessionData>)children
                    : Array.Empty<SessionData>());
        return gm;
    }

    private static ConferenceRegistry CreateRegistry(
        SessionEventStreamRouter router,
        Mock<ISessionGroupManager> groupManager)
        => new(router, Mock.Of<ISessionManager>(), groupManager.Object,
            new TaskSessionResolver(groupManager.Object));

    [Fact]
    public async Task Rebind_ShouldEnumerateChildrenIntoWindows()
    {
        var parentId = "parent1";
        var child = CreateChild("child1", "call-1");
        var parentChannel = Channel.CreateUnbounded<IMessageEvent>();
        var gm = CreateGroupManager(new() { [parentId] = new[] { child } });
        var orchestrator = CreateOrchestratorMock(new Dictionary<string, Channel<IMessageEvent>> { [parentId] = parentChannel });

        using var router = CreateRouter(orchestrator);
        var registry = CreateRegistry(router, gm);
        registry.Rebind(parentId);
        await Task.Delay(200);

        registry.Windows.Should().ContainSingle(w => w.SessionId == "child1");
    }

    [Fact]
    public async Task OnEvent_TaskToolCall_ShouldAddNewWindowAndRaiseChanged()
    {
        var parentId = "parent1";
        var child = CreateChild("child1", "call-1");
        var parentChannel = Channel.CreateUnbounded<IMessageEvent>();
        var gm = new Mock<ISessionGroupManager>();
        // 初始枚举返回空（模拟子会话尚未出现），验证"动态识别"路径独立生效
        gm.SetupSequence(m => m.ListChildrenAsync(parentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SessionData>())
            .ReturnsAsync(new[] { child });
        var orchestrator = CreateOrchestratorMock(new Dictionary<string, Channel<IMessageEvent>> { [parentId] = parentChannel });

        using var router = CreateRouter(orchestrator);
        var registry = CreateRegistry(router, gm);
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
        var child = CreateChild("child1", "call-1");
        var parentChannel = Channel.CreateUnbounded<IMessageEvent>();
        var gm = CreateGroupManager(new() { [parentId] = new[] { child } });
        var orchestrator = CreateOrchestratorMock(new Dictionary<string, Channel<IMessageEvent>> { [parentId] = parentChannel });

        using var router = CreateRouter(orchestrator);
        var registry = CreateRegistry(router, gm);
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
    public async Task CompletionEvent_ShouldNotRemoveWindow()
    {
        var parentId = "parent1";
        var child = CreateChild("child1", "call-1");
        var parentChannel = Channel.CreateUnbounded<IMessageEvent>();
        var gm = CreateGroupManager(new() { [parentId] = new[] { child } });
        var orchestrator = CreateOrchestratorMock(new Dictionary<string, Channel<IMessageEvent>> { [parentId] = parentChannel });

        using var router = CreateRouter(orchestrator);
        var registry = CreateRegistry(router, gm);
        registry.Rebind(parentId);
        await Task.Delay(200);
        registry.Windows.Should().ContainSingle(w => w.SessionId == "child1");

        await parentChannel.Writer.WriteAsync(new ExecutionCompleteEvent
        {
            SessionId = parentId, ExecutionId = "e1",
            Status = Seeing.Agent.Abstractions.Execution.ExecutionStatus.Completed
        });
        await Task.Delay(200);

        registry.Windows.Should().ContainSingle(w => w.SessionId == "child1");
    }

    [Fact]
    public async Task RemoveWindows_ShouldRemoveMatchingAndRaiseChanged()
    {
        var parentId = "parent1";
        var child1 = CreateChild("child1", "call-1");
        var child2 = CreateChild("child2", "call-2");
        var parentChannel = Channel.CreateUnbounded<IMessageEvent>();
        var gm = CreateGroupManager(new() { [parentId] = new[] { child1, child2 } });
        var orchestrator = CreateOrchestratorMock(new Dictionary<string, Channel<IMessageEvent>> { [parentId] = parentChannel });

        using var router = CreateRouter(orchestrator);
        var registry = CreateRegistry(router, gm);
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
        var child = CreateChild("child1", "call-1");
        var parentChannel = Channel.CreateUnbounded<IMessageEvent>();
        var gm = CreateGroupManager(new() { [parentId] = new[] { child } });
        var orchestrator = CreateOrchestratorMock(new Dictionary<string, Channel<IMessageEvent>> { [parentId] = parentChannel });

        using var router = CreateRouter(orchestrator);
        var registry = CreateRegistry(router, gm);
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
        var childA = CreateChild("childA", "call-a");
        var childB = CreateChild("childB", "call-b");
        var channelA = Channel.CreateUnbounded<IMessageEvent>();
        var channelB = Channel.CreateUnbounded<IMessageEvent>();
        var gm = CreateGroupManager(new()
        {
            [parentA] = new[] { childA },
            [parentB] = new[] { childB }
        });
        var orchestrator = CreateOrchestratorMock(new Dictionary<string, Channel<IMessageEvent>>
        {
            [parentA] = channelA, [parentB] = channelB
        });

        using var router = CreateRouter(orchestrator);
        var registry = CreateRegistry(router, gm);

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
