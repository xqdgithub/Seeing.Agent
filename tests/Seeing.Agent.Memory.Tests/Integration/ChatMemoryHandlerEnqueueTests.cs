using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Core.Hooks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Configuration;
using Seeing.Agent.Memory.Core;
using Seeing.Agent.Memory.Core.Models;
using Seeing.Agent.Memory.Integration;
using Xunit;

namespace Seeing.Agent.Memory.Tests.Integration;

public class ChatMemoryHandlerEnqueueTests
{
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
            NullLogger<ChatMemoryHandler>.Instance);

        var payload = HookPayload.FireAndForget(
            HookRegistry.ChatAfterComplete,
            "session-1",
            result: new Dictionary<string, object?>
            {
                ["content"] = "用户偏好使用深色主题，并要求默认语言为中文。"
            });

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
            NullLogger<ChatMemoryHandler>.Instance);

        var payload = HookPayload.FireAndForget(
            HookRegistry.ChatAfterComplete,
            "session-1",
            result: new Dictionary<string, object?> { ["content"] = "anything long enough" });

        var result = await handler.ExecuteAsync(payload);
        result.Should().Be(HookResult.Success);
        buffer.GetPendingCount("session-1").Should().Be(0);
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
            result: new Dictionary<string, object?> { ["output"] = "lots of tool output that would waste extraction tokens" });

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
            result: new Dictionary<string, object?> { ["output"] = "tool output" });

        (await handler.ExecuteAsync(payload)).Should().Be(HookResult.Success);
    }
}

public class AgentTurnMemoryHandlerTests
{
    [Fact]
    public async Task ExecuteAsync_ShouldCallFlushAfterTurn()
    {
        var flush = new Mock<IMemoryFlushService>();
        flush.Setup(f => f.TryFlushAfterTurn("session-1")).Returns(true);

        var handler = new AgentTurnMemoryHandler(
            flush.Object,
            MemoryTestOptions.Monitor(new MemoryOptions
            {
                Extraction = new MemoryExtractionOptions { ExtractEveryNTurns = 10 }
            }),
            NullLogger<AgentTurnMemoryHandler>.Instance);

        var payload = HookPayload.FireAndForget(
            HookRegistry.AgentAfterInvoke,
            "session-1",
            input: new Dictionary<string, object?> { ["agentName"] = "build", ["success"] = true });

        await handler.ExecuteAsync(payload);
        flush.Verify(f => f.TryFlushAfterTurn("session-1"), Times.Once);
    }
}
