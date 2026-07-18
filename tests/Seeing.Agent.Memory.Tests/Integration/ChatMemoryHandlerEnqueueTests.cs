using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Configuration;
using Seeing.Agent.Memory.Core.Models;
using Seeing.Agent.Memory.Integration;
using Xunit;

namespace Seeing.Agent.Memory.Tests.Integration;

public class ChatMemoryHandlerEnqueueTests
{
    [Fact]
    public async Task ExecuteAsync_WhenAutoCapture_ShouldEnqueueAndNotSave()
    {
        var queue = new Mock<IMemoryWorkQueue>();
        queue.Setup(q => q.TryEnqueue(It.IsAny<MemoryCandidate>())).Returns(true);

        var handler = new ChatMemoryHandler(
            queue.Object,
            Options.Create(new MemoryOptions()),
            new Seeing.Agent.Memory.Core.SessionActivityTracker(),
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
        queue.Verify(q => q.TryEnqueue(It.Is<MemoryCandidate>(c =>
            c.Source == MemorySource.Chat
            && c.SessionId == "session-1"
            && c.Snippet.Contains("深色主题"))), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WhenAutoCaptureDisabled_ShouldNotEnqueue()
    {
        var queue = new Mock<IMemoryWorkQueue>(MockBehavior.Strict);
        var handler = new ChatMemoryHandler(
            queue.Object,
            Options.Create(new MemoryOptions
            {
                Capture = new MemoryCaptureOptions { AutoCapture = false }
            }),
            new Seeing.Agent.Memory.Core.SessionActivityTracker(),
            NullLogger<ChatMemoryHandler>.Instance);

        var payload = HookPayload.FireAndForget(
            HookRegistry.ChatAfterComplete,
            "session-1",
            result: new Dictionary<string, object?> { ["content"] = "anything long enough" });

        var result = await handler.ExecuteAsync(payload);
        result.Should().Be(HookResult.Success);
    }
}
