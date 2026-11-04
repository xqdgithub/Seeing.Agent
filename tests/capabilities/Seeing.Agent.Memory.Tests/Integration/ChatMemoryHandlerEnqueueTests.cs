using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Configuration;
using Seeing.Agent.Memory.Core;
using Seeing.Agent.Memory.Core.Models;
using Seeing.Agent.Memory.Integration;
using Seeing.Session.Core;
using Seeing.Session.Management;
using Xunit;

namespace Seeing.Agent.Memory.Tests.Integration;

public class ChatMemoryHandlerEnqueueTests
{
    /// <summary>
    /// <c>llm.after_complete</c> 的能力键值：H-3 的 chat 守卫要求载荷显式声明能力，
    /// 值一律出自 <see cref="LlmCapabilityMap"/>，测试也不手拼字符串。
    /// </summary>
    private static readonly string ChatCapability = LlmCapabilityMap.ToHookValue(LlmCapabilityKind.Chat);
    [Fact]
    public async Task ExecuteAsync_WhenAutoCapture_ShouldBufferAndNotEnqueuePipeline()
    {
        var buffer = new SessionMemoryBuffer(MemoryTestOptions.Monitor());
        var flush = new Mock<IMemoryFlushService>(MockBehavior.Strict);
        var filter = new Mock<IMemoryHeuristicFilter>();
        filter.Setup(f => f.Evaluate(It.IsAny<MemoryCandidate>()))
            .Returns(new FilterDecision(true, null));

        var handler = new ChatMemoryHandler(
            buffer,
            flush.Object,
            filter.Object,
            MemoryTestOptions.Monitor(),
            new SessionActivityTracker(),
            sessionManager: null,
            NullLogger<ChatMemoryHandler>.Instance);

        var payload = HookPayload.FireAndForget(
            HookRegistry.LlmAfterComplete,
            "session-1",
            result: new Dictionary<string, object?>
            {
                [HookDataContract.LlmAfterComplete.Capability.Name] = ChatCapability,
                ["content"] = "用户偏好使用深色主题，并要求默认语言为中文。"
            }, cancellationToken: TestContext.Current.CancellationToken);

        var result = await handler.ExecuteAsync(payload);

        result.Should().Be(HookResult.Success);
        buffer.GetPendingCount("session-1").Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_WhenAutoCaptureDisabled_ShouldNotBuffer()
    {
        var buffer = new SessionMemoryBuffer(MemoryTestOptions.Monitor());
        var flush = new Mock<IMemoryFlushService>(MockBehavior.Strict);
        var filter = new Mock<IMemoryHeuristicFilter>(MockBehavior.Strict);

        var handler = new ChatMemoryHandler(
            buffer,
            flush.Object,
            filter.Object,
            MemoryTestOptions.Monitor(new MemoryOptions
            {
                Capture = new MemoryCaptureOptions { AutoCapture = false }
            }),
            new SessionActivityTracker(),
            sessionManager: null,
            NullLogger<ChatMemoryHandler>.Instance);

        var payload = HookPayload.FireAndForget(
            HookRegistry.LlmAfterComplete,
            "session-1",
            result: new Dictionary<string, object?>
            {
                [HookDataContract.LlmAfterComplete.Capability.Name] = ChatCapability,
                ["content"] = "anything long enough"
            }, cancellationToken: TestContext.Current.CancellationToken);

        var result = await handler.ExecuteAsync(payload);
        result.Should().Be(HookResult.Success);
        buffer.GetPendingCount("session-1").Should().Be(0);
    }

    [Theory]
    [InlineData(SessionOrigin.Reflection)]
    [InlineData(SessionOrigin.Scheduler)]
    [InlineData(SessionOrigin.Heartbeat)]
    public async Task ExecuteAsync_When_Platform_Internal_Session_Should_Not_Buffer(SessionOrigin origin)
    {
        // 复盘会话的 assistant 输出是分析结论，不是用户对话内容。
        // 收进记忆会污染召回，而且该会话随后会被删除——条目连会话都查不到。
        var buffer = new SessionMemoryBuffer(MemoryTestOptions.Monitor());
        var flush = new Mock<IMemoryFlushService>(MockBehavior.Strict);
        var filter = new Mock<IMemoryHeuristicFilter>();
        filter.Setup(f => f.Evaluate(It.IsAny<MemoryCandidate>()))
            .Returns(new FilterDecision(true, null));

        var sessions = new Mock<ISessionManager>();
        sessions.Setup(m => m.Get("session-1")).Returns(new SessionData
        {
            Id = "session-1",
            Kind = SessionKind.SubAgent,
            Origin = origin
        });

        var handler = new ChatMemoryHandler(
            buffer,
            flush.Object,
            filter.Object,
            MemoryTestOptions.Monitor(),
            new SessionActivityTracker(),
            sessions.Object,
            NullLogger<ChatMemoryHandler>.Instance);

        var payload = HookPayload.FireAndForget(
            HookRegistry.LlmAfterComplete,
            "session-1",
            result: new Dictionary<string, object?>
            {
                [HookDataContract.LlmAfterComplete.Capability.Name] = ChatCapability,
                ["content"] = "复盘结论：建议把这条流程沉淀为技能，内容足够长以通过最小长度门槛"
            },
            cancellationToken: TestContext.Current.CancellationToken);

        var result = await handler.ExecuteAsync(payload);

        result.Should().Be(HookResult.Success);
        buffer.GetPendingCount("session-1").Should().Be(0,
            "平台内部会话的产出不得进入记忆管道");
    }

    [Fact]
    public async Task ExecuteAsync_When_Session_Unknown_Should_Still_Buffer()
    {
        // 读不到会话时保守收录：不把未知来源误判为内部会话而丢掉真实用户内容。
        var buffer = new SessionMemoryBuffer(MemoryTestOptions.Monitor());
        var flush = new Mock<IMemoryFlushService>(MockBehavior.Strict);
        var filter = new Mock<IMemoryHeuristicFilter>();
        filter.Setup(f => f.Evaluate(It.IsAny<MemoryCandidate>()))
            .Returns(new FilterDecision(true, null));

        var sessions = new Mock<ISessionManager>();
        sessions.Setup(m => m.Get(It.IsAny<string>())).Returns((SessionData?)null);

        var handler = new ChatMemoryHandler(
            buffer,
            flush.Object,
            filter.Object,
            MemoryTestOptions.Monitor(),
            new SessionActivityTracker(),
            sessions.Object,
            NullLogger<ChatMemoryHandler>.Instance);

        var payload = HookPayload.FireAndForget(
            HookRegistry.LlmAfterComplete,
            "session-1",
            result: new Dictionary<string, object?>
            {
                [HookDataContract.LlmAfterComplete.Capability.Name] = ChatCapability,
                ["content"] = "用户偏好使用深色主题，并要求默认语言为中文"
            },
            cancellationToken: TestContext.Current.CancellationToken);

        await handler.ExecuteAsync(payload);

        buffer.GetPendingCount("session-1").Should().Be(1,
            "会话不在内存中时不得据此丢弃内容（可能是尚未加载的真实用户会话）");
    }
}

public class ToolMemoryHandlerTests
{
    [Fact]
    public async Task ExecuteAsync_WhenCaptureToolsDisabled_ShouldNoOp()
    {
        var handler = new ToolMemoryHandler(
            MemoryTestOptions.Monitor(new MemoryOptions
            {
                Capture = new MemoryCaptureOptions { CaptureTools = false }
            }),
            NullLogger<ToolMemoryHandler>.Instance);

        var payload = HookPayload.FireAndForget(
            HookRegistry.ToolExecuteAfter,
            "session-1",
            input: new Dictionary<string, object?> { ["toolId"] = "bash", ["callId"] = "c1" },
            result: new Dictionary<string, object?> { ["output"] = "lots of tool output that would waste extraction tokens" }, TestContext.Current.CancellationToken);

        var result = await handler.ExecuteAsync(payload);
        result.Should().Be(HookResult.Success);
    }

    [Fact]
    public async Task ExecuteAsync_WhenCaptureToolsEnabled_StillDoesNotCapture()
    {
        var handler = new ToolMemoryHandler(
            MemoryTestOptions.Monitor(new MemoryOptions
            {
                Capture = new MemoryCaptureOptions { CaptureTools = true }
            }),
            NullLogger<ToolMemoryHandler>.Instance);

        var payload = HookPayload.FireAndForget(
            HookRegistry.ToolExecuteAfter,
            "session-1",
            input: new Dictionary<string, object?> { ["toolId"] = "bash", ["callId"] = "c1" },
            result: new Dictionary<string, object?> { ["output"] = "tool output" }, TestContext.Current.CancellationToken);

        (await handler.ExecuteAsync(payload)).Should().Be(HookResult.Success);
    }
}
