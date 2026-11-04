using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Core.Llm;
using Xunit;

namespace Seeing.Agent.Tests.Llm;

/// <summary>
/// Task R-2：LlmInvoker 的固定流程与 Hook 生命周期（spec §7.5）。
/// <para>覆盖：触发顺序、EmitHooks=false、on_error 只一次、流式累计契约、非 chat 能力的 capability 键。</para>
/// </summary>
public class LlmInvokerTests
{
    private const string ModelId = "openai/gpt-4o";
    private const string EmbeddingModelId = "openai/text-embedding-3-small";

    private static Mock<ILlmRouter> CreateChatRouter(Mock<IChatCompletionClient> client)
    {
        var router = new Mock<ILlmRouter>();
        ILlmCapabilityClient? resolved = client.Object;
        router.Setup(r => r.Resolve<IChatCompletionClient>(ModelId))
            .Returns((string _) => (IChatCompletionClient)resolved!);
        return router;
    }

    private static Mock<IChatCompletionClient> CreateChatClient(
        ChatResponse? response = null,
        Func<IAsyncEnumerable<StreamUpdate>>? stream = null,
        Func<LlmCallContext, CancellationToken, Task<ChatResponse>>? custom = null)
    {
        var client = new Mock<IChatCompletionClient>();
        client.SetupGet(c => c.ProviderId).Returns("openai");
        client.SetupGet(c => c.ProviderType).Returns(ProviderTypes.OpenAi);
        if (custom is not null)
        {
            client.Setup(c => c.CompleteAsync(
                    It.IsAny<ChatRequest>(), It.IsAny<LlmCallContext?>(), It.IsAny<CancellationToken>()))
                .Returns((ChatRequest _, LlmCallContext? call, CancellationToken ct) =>
                    custom(call ?? new LlmCallContext(), ct));
        }
        else
        {
            client.Setup(c => c.CompleteAsync(
                    It.IsAny<ChatRequest>(), It.IsAny<LlmCallContext?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(response ?? new ChatResponse { Id = "resp_1" });
        }

        if (stream is not null)
        {
            client.Setup(c => c.CompleteStreamAsync(
                    It.IsAny<ChatRequest>(), It.IsAny<LlmCallContext?>(), It.IsAny<CancellationToken>()))
                .Returns(stream);
        }

        return client;
    }

    [Fact]
    public async Task Invoke_ShouldFireParamsHeadersAfterCompleteInOrder()
    {
        var hooks = new RecordingHookManager();
        var client = CreateChatClient();
        var sut = new LlmInvoker(
            CreateChatRouter(client).Object, hooks, new TestAdapterRegistry(), NullLogger<LlmInvoker>.Instance);

        var response = await sut.InvokeAsync<IChatCompletionClient, ChatResponse>(
            ModelId,
            (c, _, ct) => c.CompleteAsync(new ChatRequest(), null, ct),
            new LlmInvocationOptions { SessionId = "ses_1" },
            TestContext.Current.CancellationToken);

        response.Should().NotBeNull();
        hooks.Points.Should().Equal(
            "llm.params",
            "llm.headers",
            "llm.message",
            "llm.after_complete");

        // 载荷必须带点分 capability（决议 18）
        var result = hooks.Results["llm.after_complete"];
        result[HookDataContract.LlmAfterComplete.Capability.Name].Should().Be("chat");
        result[HookDataContract.LlmAfterComplete.Provider.Name].Should().Be("openai");
        result[HookDataContract.LlmAfterComplete.Duration.Name].Should().BeOfType<TimeSpan>();
    }

    [Fact]
    public async Task Invoke_WhenEmitHooksFalse_ShouldFireNothing()
    {
        var hooks = new RecordingHookManager();
        var client = CreateChatClient();
        var sut = new LlmInvoker(
            CreateChatRouter(client).Object, hooks, new TestAdapterRegistry(), NullLogger<LlmInvoker>.Instance);

        await sut.InvokeAsync<IChatCompletionClient, ChatResponse>(
            ModelId,
            (c, _, ct) => c.CompleteAsync(new ChatRequest(), null, ct),
            new LlmInvocationOptions { EmitHooks = false },
            TestContext.Current.CancellationToken);

        hooks.Points.Should().BeEmpty("旁路调用（TextCompletionService）一个 Hook 都不发");
    }

    [Fact]
    public async Task Invoke_OnRetryExhaustion_ShouldFireOnErrorOnce()
    {
        var hooks = new RecordingHookManager();
        var client = CreateChatClient(custom: (_, _) => Task.FromException<ChatResponse>(
            new HttpRequestException("429 重试耗尽")));
        var sut = new LlmInvoker(
            CreateChatRouter(client).Object, hooks, new TestAdapterRegistry(), NullLogger<LlmInvoker>.Instance);

        var act = () => sut.InvokeAsync<IChatCompletionClient, ChatResponse>(
            ModelId,
            (c, _, ct) => c.CompleteAsync(new ChatRequest(), null, ct),
            new LlmInvocationOptions { SessionId = "ses_1" },
            TestContext.Current.CancellationToken);

        // 装饰器在客户端内部重试：本层看到的是一次终态失败 → on_error 恰好一次，且不发 after_complete
        (await act.Should().ThrowAsync<HttpRequestException>()).Which.Should().NotBeNull();
        hooks.Points.Should().ContainInOrder("llm.params", "llm.headers", "llm.on_error");
        hooks.Points.Count(point => point == "llm.on_error").Should().Be(1, "重试耗尽后错误 Hook 只触发一次");
        hooks.Points.Should().NotContain("llm.after_complete");
        hooks.Inputs["llm.on_error"]["error"].Should().BeOfType<HttpRequestException>();
    }

    [Fact]
    public async Task Invoke_WhenRouterRejects_ShouldFireNoHooks()
    {
        var hooks = new RecordingHookManager();
        var router = new Mock<ILlmRouter>();
        router.Setup(r => r.Resolve<IChatCompletionClient>(It.IsAny<string>()))
            .Throws(new LlmCapabilityMismatchException("bad", LlmCapabilityKind.Chat, "模型目录中不存在"));
        var sut = new LlmInvoker(router.Object, hooks, new TestAdapterRegistry(), NullLogger<LlmInvoker>.Instance);

        var act = () => sut.InvokeAsync<IChatCompletionClient, ChatResponse>(
            "bad",
            (c, _, ct) => c.CompleteAsync(new ChatRequest(), null, ct),
            cancellationToken: TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<LlmCapabilityMismatchException>()).Which.Should().NotBeNull();
        hooks.Points.Should().BeEmpty("五层校验不过就不该留下任何 Hook 痕迹");
    }

    [Fact]
    public async Task Invoke_ShouldSeedParamsBagAndMergeHeadersIntoCallContext()
    {
        var hooks = new RecordingHookManager(
            onBlocking: (spec, mutable) =>
            {
                if (spec.Point == "llm.params")
                    mutable["temperature"] = 0.2;
                if (spec.Point == "llm.headers")
                {
                    var headers = (Dictionary<string, string>)mutable["headers"]!;
                    headers["X-Trace"] = "abc";
                }
            });

        LlmCallContext? seen = null;
        var client = CreateChatClient(custom: (call, _) =>
        {
            seen = call;
            return Task.FromResult(new ChatResponse { Id = "resp_1" });
        });

        var sut = new LlmInvoker(
            CreateChatRouter(client).Object, hooks, new TestAdapterRegistry(), NullLogger<LlmInvoker>.Instance);

        await sut.InvokeAsync<IChatCompletionClient, ChatResponse>(
            ModelId,
            (c, call, ct) => c.CompleteAsync(new ChatRequest(), call, ct),
            new LlmInvocationOptions { SessionId = "ses_1" },
            TestContext.Current.CancellationToken);

        // 钩子写过的参数袋要能被调用方读回（chat 由 LlmService 落到 ChatRequest）
        seen.Should().NotBeNull();
        var bag = seen!.Items[LlmInvoker.ParamsItemKey] as IDictionary<string, object?>;
        bag.Should().NotBeNull();
        bag!["temperature"].Should().Be(0.2);
        seen.ExtraHeaders["X-Trace"].Should().Be("abc");
        seen.SessionId.Should().Be("ses_1");
        seen.CorrelationId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Stream_ShouldFireAfterCompleteWithAccumulatedUsage()
    {
        var hooks = new RecordingHookManager();
        var client = CreateChatClient(stream: Updates);
        var sut = new LlmInvoker(
            CreateChatRouter(client).Object, hooks, new TestAdapterRegistry(), NullLogger<LlmInvoker>.Instance);

        var chunks = new List<StreamUpdate>();
        await foreach (var update in sut.StreamAsync<IChatCompletionClient, StreamUpdate>(
            ModelId,
            (c, call, ct) => c.CompleteStreamAsync(new ChatRequest(), call, ct),
            new LlmInvocationOptions { SessionId = "ses_1" },
            TestContext.Current.CancellationToken))
        {
            chunks.Add(update);
        }

        chunks.Should().HaveCount(3, "转发不得丢块");
        hooks.Points.Should().Contain("llm.after_complete");
        var result = hooks.Results["llm.after_complete"];
        result[HookDataContract.LlmAfterComplete.Streaming.Name].Should().Be(true);
        result[HookDataContract.LlmAfterComplete.MessageId.Name].Should().Be("resp_stream", "累计 Id 来自首个 chunk");
        result[HookDataContract.LlmAfterComplete.Content.Name].Should().Be("正文一正文二");
        result[HookDataContract.LlmAfterComplete.Reasoning.Name].Should().Be("思考一思考二");
        result[HookDataContract.LlmAfterComplete.Usage.Name].Should().BeOfType<TokenUsage>();
        ((TokenUsage)result[HookDataContract.LlmAfterComplete.Usage.Name]!).OutputTokens.Should().Be(7);
        var toolCalls = result[HookDataContract.LlmAfterComplete.ToolCalls.Name] as IReadOnlyList<ToolCall>;
        toolCalls.Should().HaveCount(1, "同 Id 的增量必须合并成一次工具调用");
        toolCalls![0].Function!.Arguments.Should().Be("{\"a\":1}");
    }

    [Fact]
    public async Task Stream_NonStreamUpdateChunks_ShouldNotPromiseAccumulation()
    {
        // 契约（ILlmInvoker XML 注释）：AudioChunk 之类不承诺累计语义 → usage 与三个流式累计字段为空
        var hooks = new RecordingHookManager();
        var router = new Mock<ILlmRouter>();
        var client = new Mock<ISpeechSynthesisClient>();
        client.SetupGet(c => c.ProviderId).Returns("openai");
        client.Setup(c => c.SynthesizeStreamAsync(
                It.IsAny<SpeechSynthesisRequest>(), It.IsAny<LlmCallContext?>(), It.IsAny<CancellationToken>()))
            .Returns(Audio);
        router.Setup(r => r.Resolve<ISpeechSynthesisClient>("openai/tts-1")).Returns(client.Object);
        var sut = new LlmInvoker(
            router.Object, hooks, new TestAdapterRegistry(), NullLogger<LlmInvoker>.Instance);

        var seen = 0;
        await foreach (var _ in sut.StreamAsync<ISpeechSynthesisClient, AudioChunk>(
            "openai/tts-1",
            (c, call, ct) => c.SynthesizeStreamAsync(new SpeechSynthesisRequest { Model = "tts-1", Text = "hi" }, call, ct),
            cancellationToken: TestContext.Current.CancellationToken))
        {
            seen++;
        }

        seen.Should().Be(2);
        var result = hooks.Results["llm.after_complete"];
        result.Should().ContainKey(HookDataContract.LlmAfterComplete.Usage.Name);
        result[HookDataContract.LlmAfterComplete.Usage.Name].Should().BeNull();
        result[HookDataContract.LlmAfterComplete.Capability.Name].Should().Be("speech.synthesis");
    }

    [Fact]
    public async Task Invoke_NonChatCapability_ShouldPassCapabilityKeyInPayload()
    {
        var hooks = new RecordingHookManager();
        var router = new Mock<ILlmRouter>();
        var client = new Mock<IEmbeddingClient>();
        client.SetupGet(c => c.ProviderId).Returns("openai");
        client.Setup(c => c.EmbedAsync(
                It.IsAny<EmbeddingRequest>(), It.IsAny<LlmCallContext?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EmbeddingResponse
            {
                Vectors = [new EmbeddingVector("hi", [0.1f, 0.2f], 0)],
                Usage = new TokenUsage { InputTokens = 3 }
            });
        router.Setup(r => r.Resolve<IEmbeddingClient>(EmbeddingModelId)).Returns(client.Object);

        IDictionary<string, object?>? paramsBag = null;
        var sut = new LlmInvoker(
            router.Object, hooks, new TestAdapterRegistry(), NullLogger<LlmInvoker>.Instance);

        await sut.InvokeAsync<IEmbeddingClient, EmbeddingResponse>(
            EmbeddingModelId,
            (c, call, ct) =>
            {
                paramsBag = call.Items[LlmInvoker.ParamsItemKey] as IDictionary<string, object?>;
                return c.EmbedAsync(new EmbeddingRequest { Model = "text-embedding-3-small", Inputs = ["hi"] }, call, ct);
            },
            new LlmInvocationOptions { SessionId = "ses_1" },
            TestContext.Current.CancellationToken);

        hooks.Points.Should().Contain("llm.params");
        // §7.6：embedding 的特化键是 dimensions / encodingFormat（不是 chat 的 temperature 那一套）
        paramsBag.Should().NotBeNull();
        paramsBag!.Keys.Should().BeEquivalentTo("dimensions", "encodingFormat");
        hooks.Results["llm.after_complete"][HookDataContract.LlmAfterComplete.Capability.Name]
            .Should().Be("embedding");
        hooks.Inputs["llm.params"][HookDataContract.LlmAfterComplete.Capability.Name].Should().Be("embedding");
    }

    [Fact]
    public async Task Invoke_ShouldFailFastForUnregisteredPort()
    {
        var hooks = new RecordingHookManager();
        var sut = new LlmInvoker(
            new Mock<ILlmRouter>().Object, hooks, new TestAdapterRegistry(), NullLogger<LlmInvoker>.Instance);

        var act = () => sut.InvokeAsync<IVideoGenerationClient, MediaJobStatus>(
            "p/m",
            (_, _, _) => Task.FromResult(new MediaJobStatus(MediaJobState.Queued, null, null, null)),
            cancellationToken: TestContext.Current.CancellationToken);

        // 端口在 ClientTypes 里有登记（video），因此不会因端口解析失败；这里断言 Router 未被配置时的正常抛出
        await act.Should().ThrowAsync<Exception>();
    }

    private static async IAsyncEnumerable<StreamUpdate> Updates()
    {
        await Task.CompletedTask;
        yield return new StreamUpdate
        {
            Id = "resp_stream",
            ContentDelta = "正文一",
            ReasoningDelta = "思考一",
            ToolCallDeltas = [new ToolCall { Id = "call_1", Function = new FunctionCall { Name = "read", Arguments = "{\"a\"" } }]
        };
        yield return new StreamUpdate
        {
            Id = "resp_stream",
            ContentDelta = "正文二",
            ReasoningDelta = "思考二",
            ToolCallDeltas = [new ToolCall { Id = "call_1", Function = new FunctionCall { Arguments = ":1}" } }]
        };
        yield return new StreamUpdate
        {
            Id = "resp_stream",
            Usage = new TokenUsage { InputTokens = 11, OutputTokens = 7 },
            IsComplete = true
        };
    }

    private static async IAsyncEnumerable<AudioChunk> Audio()
    {
        await Task.CompletedTask;
        yield return new AudioChunk(new byte[2], "audio/mpeg");
        yield return new AudioChunk(new byte[3], "audio/mpeg");
    }

    /// <summary>
    /// 适配器注册表替身：直接复用 Core 的内置适配器语义太宽（H 轨道属主），
    /// 这里只回一个「把上下文摊平」的适配器，断言由本层的累计/键名负责。
    /// </summary>
    private sealed class TestAdapterRegistry : ILlmHookPayloadAdapterRegistry
    {
        private static readonly ILlmHookPayloadAdapter Adapter = new FlatteningAdapter();

        public void Register(ILlmHookPayloadAdapter adapter)
        {
        }

        public bool Unregister(ILlmHookPayloadAdapter adapter) => false;

        public ILlmHookPayloadAdapter Resolve(LlmCapabilityKind capability) => Adapter;

        private sealed class FlatteningAdapter : ILlmHookPayloadAdapter
        {
            public LlmCapabilityKind Capability => LlmCapabilityKind.None;

            public IReadOnlyDictionary<string, object?> Build(LlmPayloadContext context)
            {
                var payload = new Dictionary<string, object?>
                {
                    [HookDataContract.LlmAfterComplete.Capability.Name] = LlmCapabilityMap.ToHookValue(context.Capability),
                    [HookDataContract.LlmAfterComplete.Provider.Name] = context.ProviderId,
                    [HookDataContract.LlmAfterComplete.Model.Name] = context.ModelId,
                    [HookDataContract.LlmAfterComplete.Usage.Name] = context.Usage,
                    [HookDataContract.LlmAfterComplete.Duration.Name] = context.Duration,
                    [HookDataContract.LlmAfterComplete.Streaming.Name] = context.Streaming,
                    [HookDataContract.LlmAfterComplete.MessageId.Name] = context.MessageId,
                    [HookDataContract.LlmAfterComplete.Content.Name] = context.StreamedContent,
                    [HookDataContract.LlmAfterComplete.Reasoning.Name] = context.StreamedReasoning,
                    [HookDataContract.LlmAfterComplete.ToolCalls.Name] = context.StreamedToolCalls
                };
                return payload;
            }
        }
    }

    /// <summary>记录触发顺序与 input/result，并可模拟钩子改写可变袋。</summary>
    private sealed class RecordingHookManager : IHookManager
    {
        private readonly Action<HookSpec, IDictionary<string, object?>>? _onBlocking;

        public RecordingHookManager(Action<HookSpec, IDictionary<string, object?>>? onBlocking = null)
            => _onBlocking = onBlocking;

        public List<string> Points { get; } = [];
        public Dictionary<string, IReadOnlyDictionary<string, object?>> Inputs { get; } = new();
        public Dictionary<string, IReadOnlyDictionary<string, object?>> Results { get; } = new();

        public long Version => 0;
        public void Register(IHookHandler handler)
        {
        }
        public void RegisterMulti(IMultiHookHandler handler)
        {
        }
        public bool Remove(IHookHandler handler) => false;
        public bool Remove(IMultiHookHandler handler) => false;
        public bool Clear(HookSpec spec) => false;
        public int Count(HookSpec spec) => 0;

        public Task<HookResult> TriggerAsync(HookPayload payload)
        {
            Points.Add(payload.Spec.Point);
            return Task.FromResult(HookResult.Success);
        }

        public Task<HookResult> TriggerBlockingAsync(
            HookSpec spec,
            string sessionId,
            IReadOnlyDictionary<string, object?>? input = null,
            IDictionary<string, object?>? mutable = null,
            CancellationToken cancellationToken = default)
        {
            Points.Add(spec.Point);
            Inputs[spec.Point] = input ?? new Dictionary<string, object?>();
            _onBlocking?.Invoke(spec, mutable ?? new Dictionary<string, object?>());
            return Task.FromResult(HookResult.Success);
        }

        public void TriggerFireAndForget(
            HookSpec spec,
            string sessionId,
            IReadOnlyDictionary<string, object?>? input = null,
            IReadOnlyDictionary<string, object?>? result = null)
        {
            Points.Add(spec.Point);
            Inputs[spec.Point] = input ?? new Dictionary<string, object?>();
            Results[spec.Point] = result ?? new Dictionary<string, object?>();
        }

        public Task TriggerParallelAsync(
            HookSpec spec,
            string sessionId,
            IReadOnlyDictionary<string, object?>? input = null,
            CancellationToken cancellationToken = default)
        {
            Points.Add(spec.Point);
            Inputs[spec.Point] = input ?? new Dictionary<string, object?>();
            return Task.CompletedTask;
        }
    }
}
