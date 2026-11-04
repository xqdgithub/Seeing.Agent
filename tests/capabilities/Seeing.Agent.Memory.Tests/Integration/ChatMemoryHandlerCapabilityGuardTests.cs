using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Configuration;
using Seeing.Agent.Memory.Core;
using Seeing.Agent.Memory.Core.Models;
using Seeing.Agent.Memory.Integration;
using Seeing.Session.Management;
using Xunit;

namespace Seeing.Agent.Memory.Tests.Integration;

/// <summary>
/// H-3：<see cref="ChatMemoryHandler"/> 的 chat 守卫（spec §7.6 消费方改造）。
/// <para><c>llm.after_complete</c> 现在承载所有能力的终态；非 chat 的载荷里没有会话对话内容，
/// 收进记忆缓冲会让抽取去消化向量数、音频字节这类东西。</para>
/// </summary>
public class ChatMemoryHandlerCapabilityGuardTests
{
    private const string Content = "用户偏好使用深色主题，并要求默认语言为中文。";

    /// <summary>能力键值出自 <see cref="LlmCapabilityMap"/>：连测试也不手拼 capability 字符串（决议 18）。</summary>
    private static readonly string ChatCapability = LlmCapabilityMap.ToHookValue(LlmCapabilityKind.Chat);

    [Fact]
    public void Spec_ShouldSubscribeLlmAfterComplete()
    {
        CreateHandler(out _).Spec.Should().Be(HookRegistry.LlmAfterComplete);
    }

    [Theory]
    [InlineData("embedding")]
    [InlineData("decision")]
    [InlineData("speech.synthesis")]
    [InlineData("speech.transcription")]
    public async Task ExecuteAsync_NonChatCapability_ShouldSkip(string capability)
    {
        var handler = CreateHandler(out var buffer);

        await handler.ExecuteAsync(Payload(capability, Content));

        buffer.GetPendingCount("session-1").Should().Be(0, "非 chat 能力不得进入记忆缓冲");
    }

    [Fact]
    public async Task ExecuteAsync_MissingCapabilityKey_ShouldSkip()
    {
        // 严格口径：载荷没声明能力就不按 chat 处理（守卫缺失会静默收脏数据）
        var handler = CreateHandler(out var buffer);
        var payload = HookPayload.FireAndForget(
            HookRegistry.LlmAfterComplete,
            "session-1",
            result: new Dictionary<string, object?>
            {
                [HookDataContract.LlmAfterComplete.Content.Name] = Content
            },
            cancellationToken: TestContext.Current.CancellationToken);

        await handler.ExecuteAsync(payload);

        buffer.GetPendingCount("session-1").Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_ChatPayload_ShouldStillBuffer()
    {
        // 守卫不得误杀正路：chat 载荷仍按原语义入缓冲
        var handler = CreateHandler(out var buffer);

        await handler.ExecuteAsync(Payload(ChatCapability, Content));

        buffer.GetPendingCount("session-1").Should().Be(1);
    }

    private static HookPayload Payload(string capability, string content) =>
        HookPayload.FireAndForget(
            HookRegistry.LlmAfterComplete,
            "session-1",
            result: new Dictionary<string, object?>
            {
                [HookDataContract.LlmAfterComplete.Capability.Name] = capability,
                [HookDataContract.LlmAfterComplete.Content.Name] = content,
            });

    private static ChatMemoryHandler CreateHandler(out SessionMemoryBuffer buffer)
    {
        buffer = new SessionMemoryBuffer(Monitor());
        var flush = new Mock<IMemoryFlushService>(MockBehavior.Strict);
        var filter = new Mock<IMemoryHeuristicFilter>();
        filter.Setup(f => f.Evaluate(It.IsAny<MemoryCandidate>()))
            .Returns(new FilterDecision(true, null));

        return new ChatMemoryHandler(
            buffer,
            flush.Object,
            filter.Object,
            Monitor(),
            new SessionActivityTracker(),
            sessionManager: null,
            NullLogger<ChatMemoryHandler>.Instance);
    }

    private static IOptionsMonitor<MemoryOptions> Monitor() =>
        Mock.Of<IOptionsMonitor<MemoryOptions>>(m => m.CurrentValue == new MemoryOptions());
}
