using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Core.Llm.Hooks;
using Xunit;

namespace Seeing.Agent.Tests.Llm;

/// <summary>
/// H-2：<c>llm.after_complete</c> 载荷适配器与注册表。
/// <para>盯三件事：通用键的点分能力名（决议 18）、能力特化键齐全、兜底一定不炸（spec §7.5）。</para>
/// </summary>
public class LlmHookPayloadAdapterTests
{
    private static readonly LlmPayloadContext BaseContext = new()
    {
        Capability = LlmCapabilityKind.Chat,
        ProviderId = "p1",
        ModelId = "m1",
        Duration = TimeSpan.FromMilliseconds(1234),
    };

    // ===== 通用键 =====

    [Theory]
    [InlineData(LlmCapabilityKind.Chat, "chat")]
    [InlineData(LlmCapabilityKind.Embedding, "embedding")]
    [InlineData(LlmCapabilityKind.Decision, "decision")]
    [InlineData(LlmCapabilityKind.SpeechSynthesis, "speech.synthesis")]
    [InlineData(LlmCapabilityKind.SpeechTranscription, "speech.transcription")]
    public void EveryBuiltInAdapter_ShouldEmitCommonKeysWithDottedCapability(
        LlmCapabilityKind capability, string expectedHookValue)
    {
        var registry = new LlmHookPayloadAdapterRegistry();
        var context = BaseContext with { Capability = capability };

        var payload = registry.Resolve(capability).Build(context);

        payload[HookDataContract.LlmAfterComplete.Capability.Name].Should().Be(expectedHookValue);
        payload[HookDataContract.LlmAfterComplete.Provider.Name].Should().Be("p1");
        payload[HookDataContract.LlmAfterComplete.Model.Name].Should().Be("m1");
        payload[HookDataContract.LlmAfterComplete.ModelId.Name].Should().Be("m1");
        payload.Should().ContainKey(HookDataContract.LlmAfterComplete.Usage.Name);
        payload[HookDataContract.LlmAfterComplete.Duration.Name].Should().Be(TimeSpan.FromMilliseconds(1234));
    }

    // ===== chat =====

    [Fact]
    public void ChatAdapter_NonStreaming_ShouldEmitContentReasoningAndToolCalls()
    {
        var response = new ChatResponse
        {
            Id = "resp_1",
            Message = new ChatMessage
            {
                Role = ChatRole.Assistant,
                Content = "正文",
                ReasoningContent = "推理",
                ToolCalls = [new ToolCall { Id = "call_1", Function = new FunctionCall { Name = "search" } }]
            },
            Usage = new TokenUsage { InputTokens = 11, OutputTokens = 22 }
        };

        var payload = new BuiltInLlmChatPayloadAdapter().Build(
            BaseContext with { Response = response, Step = 3 });

        payload[HookDataContract.LlmAfterComplete.Content.Name].Should().Be("正文");
        payload[HookDataContract.LlmAfterComplete.Reasoning.Name].Should().Be("推理");
        payload[HookDataContract.LlmAfterComplete.ToolCalls.Name].Should().BeSameAs(response.Message.ToolCalls);
        payload[HookDataContract.LlmAfterComplete.MessageId.Name].Should().Be("resp_1");
        payload[HookDataContract.LlmAfterComplete.Streaming.Name].Should().Be(false);
        payload[HookDataContract.LlmAfterComplete.Step.Name].Should().Be(3);
        payload[HookDataContract.LlmAfterComplete.Response.Name].Should().BeSameAs(response);
        // 适配器负责把响应的 usage 抬进载荷：Invoker 不认得每种 DTO
        payload[HookDataContract.LlmAfterComplete.Usage.Name].Should().BeSameAs(response.Usage);
    }

    [Fact]
    public void ChatAdapter_Streaming_ShouldReadAccumulatedFieldsInsteadOfResponse()
    {
        var streamedToolCalls = new[] { new ToolCall { Id = "call_9" } };

        var payload = new BuiltInLlmChatPayloadAdapter().Build(BaseContext with
        {
            Streaming = true,
            // 流式时 Response 为 null：正文只可能来自 Invoker 累计好的字段
            StreamedContent = "累计正文",
            StreamedReasoning = "累计推理",
            StreamedToolCalls = streamedToolCalls,
            MessageId = "chunk_1",
            Usage = new TokenUsage { InputTokens = 5, OutputTokens = 6 },
        });

        payload[HookDataContract.LlmAfterComplete.Content.Name].Should().Be("累计正文");
        payload[HookDataContract.LlmAfterComplete.Reasoning.Name].Should().Be("累计推理");
        payload[HookDataContract.LlmAfterComplete.ToolCalls.Name].Should().BeSameAs(streamedToolCalls);
        payload[HookDataContract.LlmAfterComplete.MessageId.Name].Should().Be("chunk_1");
        payload[HookDataContract.LlmAfterComplete.Streaming.Name].Should().Be(true);
        payload[HookDataContract.LlmAfterComplete.Usage.Name]
            .Should().BeOfType<TokenUsage>().Which.InputTokens.Should().Be(5);
    }

    [Fact]
    public void ChatAdapter_NullResponse_ShouldEmitChatKeysAsNullNotThrow()
    {
        var payload = new BuiltInLlmChatPayloadAdapter().Build(BaseContext with { Capability = LlmCapabilityKind.Chat });

        payload.Should().ContainKey(HookDataContract.LlmAfterComplete.Content.Name);
        payload[HookDataContract.LlmAfterComplete.Content.Name].Should().BeNull();
        payload[HookDataContract.LlmAfterComplete.MessageId.Name].Should().BeNull("空串不是合法消息 ID");
    }

    // ===== embedding / decision / speech =====

    [Fact]
    public void EmbeddingAdapter_ShouldEmitVectorCountAndDimensions()
    {
        var response = new EmbeddingResponse
        {
            Vectors =
            [
                new EmbeddingVector("a", [0.1f, 0.2f, 0.3f], 0),
                new EmbeddingVector("b", [0.4f, 0.5f, 0.6f], 1)
            ],
            Usage = new TokenUsage { InputTokens = 7 }
        };

        var payload = new BuiltInLlmEmbeddingPayloadAdapter().Build(
            BaseContext with { Capability = LlmCapabilityKind.Embedding, Response = response });

        payload[HookDataContract.LlmAfterComplete.VectorCount.Name].Should().Be(2);
        payload[HookDataContract.LlmAfterComplete.Dimensions.Name].Should().Be(3);
        payload.Should().NotContainKey(HookDataContract.LlmAfterComplete.Content.Name);
        payload[HookDataContract.LlmAfterComplete.Usage.Name].Should().BeSameAs(response.Usage);
    }

    [Fact]
    public void DecisionAdapter_ShouldEmitAnswers()
    {
        var response = new DecisionResponse { Model = "jev-1" };
        response.Answers["route"] = new DecisionAnswer { Type = "choice", Choice = "billing" };

        var payload = new BuiltInLlmDecisionPayloadAdapter().Build(
            BaseContext with { Capability = LlmCapabilityKind.Decision, Response = response });

        payload[HookDataContract.LlmAfterComplete.Answers.Name].Should().BeSameAs(response.Answers);
    }

    [Fact]
    public void SpeechSynthesisAdapter_ShouldEmitAudioBytesOnly()
    {
        var response = new SpeechSynthesisResult(new byte[64], "audio/mpeg", null);

        var payload = new BuiltInLlmSpeechSynthesisPayloadAdapter().Build(BaseContext with
        {
            Capability = LlmCapabilityKind.SpeechSynthesis,
            Response = response
        });

        payload[HookDataContract.LlmAfterComplete.Capability.Name].Should().Be("speech.synthesis");
        payload[HookDataContract.LlmAfterComplete.AudioBytes.Name].Should().Be(64);
        payload.Should().NotContainKey(HookDataContract.LlmAfterComplete.Content.Name);
    }

    [Fact]
    public void SpeechTranscriptionAdapter_ShouldEmitTranscribedTextAsContent()
    {
        var response = new TranscriptionResult("转写全文", null, new TokenUsage { InputTokens = 3 });

        var payload = new BuiltInLlmSpeechTranscriptionPayloadAdapter().Build(BaseContext with
        {
            Capability = LlmCapabilityKind.SpeechTranscription,
            Response = response
        });

        payload[HookDataContract.LlmAfterComplete.Capability.Name].Should().Be("speech.transcription");
        payload[HookDataContract.LlmAfterComplete.Content.Name].Should().Be("转写全文");
        payload[HookDataContract.LlmAfterComplete.Usage.Name].Should().BeSameAs(response.Usage);
    }

    // ===== 兜底 =====

    [Theory]
    [InlineData(LlmCapabilityKind.Rerank)]
    [InlineData(LlmCapabilityKind.ImageGeneration)]
    [InlineData(LlmCapabilityKind.VideoGeneration)]
    [InlineData(LlmCapabilityKind.None)]
    public void Fallback_ShouldEmitOnlyCommonKeys(LlmCapabilityKind capability)
    {
        var payload = new FallbackLlmHookPayloadAdapter().Build(
            BaseContext with { Capability = capability, Response = new { anything = 1 } });

        payload.Keys.Should().BeEquivalentTo(
        [
            HookDataContract.LlmAfterComplete.Capability.Name,
            HookDataContract.LlmAfterComplete.Provider.Name,
            HookDataContract.LlmAfterComplete.Model.Name,
            HookDataContract.LlmAfterComplete.ModelId.Name,
            HookDataContract.LlmAfterComplete.Usage.Name,
            HookDataContract.LlmAfterComplete.Duration.Name,
        ]);
    }

    [Fact]
    public void Fallback_ShouldNotThrow_EvenWhenContextIsBroken()
    {
        var adapter = new FallbackLlmHookPayloadAdapter();

        // 兜底的契约是「一定有载荷、一定不炸」：连 null 上下文（上游写错）也只能产出通用键
        var act = () => adapter.Build(null!);

        act.Should().NotThrow();
        act().Should().NotBeEmpty();
    }

    [Fact]
    public void BuiltInAdapter_ShouldFallBackToCommonKeys_WhenSpecializationThrows()
    {
        // 插件/内置适配器的特化逻辑出事时，不得让一次成功的 LLM 调用变成失败
        var payload = new ThrowingAdapter().Build(BaseContext);

        payload.Should().ContainKey(HookDataContract.LlmAfterComplete.Model.Name);
    }

    // ===== 注册表 =====

    [Fact]
    public void Registry_Resolve_ShouldGiveBuiltInBaseline_WithoutAnyRegistration()
    {
        var registry = new LlmHookPayloadAdapterRegistry();

        registry.Resolve(LlmCapabilityKind.Chat).Should().BeOfType<BuiltInLlmChatPayloadAdapter>();
        registry.Resolve(LlmCapabilityKind.Embedding).Should().BeOfType<BuiltInLlmEmbeddingPayloadAdapter>();
        registry.Resolve(LlmCapabilityKind.Decision).Should().BeOfType<BuiltInLlmDecisionPayloadAdapter>();
        registry.Resolve(LlmCapabilityKind.SpeechSynthesis).Should().BeOfType<BuiltInLlmSpeechSynthesisPayloadAdapter>();
        registry.Resolve(LlmCapabilityKind.SpeechTranscription)
            .Should().BeOfType<BuiltInLlmSpeechTranscriptionPayloadAdapter>();
    }

    [Fact]
    public void Registry_Resolve_UnimplementedCapability_ShouldUseFallbackWithoutThrowing()
    {
        var registry = new LlmHookPayloadAdapterRegistry();

        var adapter = registry.Resolve(LlmCapabilityKind.VideoGeneration);

        adapter.Should().BeOfType<FallbackLlmHookPayloadAdapter>();
        adapter.Build(BaseContext with { Capability = LlmCapabilityKind.VideoGeneration })
            .Should().ContainKey(HookDataContract.LlmAfterComplete.Model.Name);
    }

    [Fact]
    public void Registry_Register_ShouldLetLaterAdapterWin_AndWarn()
    {
        var logger = new Mock<ILogger<LlmHookPayloadAdapterRegistry>>();
        var registry = new LlmHookPayloadAdapterRegistry(logger: logger.Object);
        var winner = new FakeAdapter(LlmCapabilityKind.Chat);

        registry.Register(winner);

        registry.Resolve(LlmCapabilityKind.Chat).Should().BeSameAs(winner, "后注册胜：插件覆盖内置");
        VerifyWarning(logger, "被覆盖");
    }

    [Fact]
    public void Registry_ShouldApplyDiAdaptersInOrder_ThenFillMissingBuiltIns()
    {
        var pluginChat = new FakeAdapter(LlmCapabilityKind.Chat);

        var registry = new LlmHookPayloadAdapterRegistry([pluginChat]);

        registry.Resolve(LlmCapabilityKind.Chat).Should().BeSameAs(pluginChat);
        registry.Resolve(LlmCapabilityKind.Embedding).Should().BeOfType<BuiltInLlmEmbeddingPayloadAdapter>();
    }

    [Fact]
    public void Registry_Unregister_ShouldOnlyRemoveTheCurrentlyActiveAdapter()
    {
        var registry = new LlmHookPayloadAdapterRegistry();
        var chat = registry.Resolve(LlmCapabilityKind.Chat);
        var winner = new FakeAdapter(LlmCapabilityKind.Chat);
        registry.Register(winner);

        registry.Unregister(chat).Should().BeFalse("它已被覆盖，不该把胜出者摘掉");
        registry.Unregister(winner).Should().BeTrue();
        registry.Resolve(LlmCapabilityKind.Chat).Should().BeOfType<FallbackLlmHookPayloadAdapter>();
    }

    [Fact]
    public void Registry_Register_Null_ShouldThrow()
    {
        var registry = new LlmHookPayloadAdapterRegistry();

        var act = () => registry.Register(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    private static void VerifyWarning(Mock<ILogger<LlmHookPayloadAdapterRegistry>> logger, string fragment) =>
        logger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains(fragment)),
                It.IsAny<Exception?>(),
                (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()),
            Times.Once);

    private sealed class FakeAdapter(LlmCapabilityKind capability) : ILlmHookPayloadAdapter
    {
        public LlmCapabilityKind Capability { get; } = capability;

        public IReadOnlyDictionary<string, object?> Build(LlmPayloadContext context) =>
            new Dictionary<string, object?> { ["marker"] = Capability };
    }

    private sealed class ThrowingAdapter : LlmHookPayloadAdapterBase
    {
        public override LlmCapabilityKind Capability => LlmCapabilityKind.Chat;

        protected override void AppendSpecialized(LlmPayloadContext context, IDictionary<string, object?> payload) =>
            throw new InvalidOperationException("装配事故");
    }
}
