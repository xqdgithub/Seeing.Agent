using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Core.Llm;
using System.Net;
using System.Runtime.CompilerServices;
using Xunit;

namespace Seeing.Agent.Tests.Llm;

/// <summary>
/// 装饰器泛型化（spec §7.7 / Task D-1）的行为锁定：八个能力端口各有重试包装、
/// 共享同一套退避与 <see cref="LlmCallContext.Items"/> 记账键、
/// 流式与轮询一旦交付过元素就绝不重开。
/// </summary>
public class RetryLlmClientDecoratorTests
{
    private static Exception Transient() =>
        new HttpRequestException("503", null, HttpStatusCode.ServiceUnavailable);

    private static Exception NonRetryable() =>
        new HttpRequestException("400", null, HttpStatusCode.BadRequest);

    /// <summary>按 inner 的能力端口分派；重试参数压到毫秒级以免测试变慢。</summary>
    private static ILlmCapabilityClient Wrap(
        ILlmCapabilityClient inner,
        int maxRetries = 0,
        int baseDelayMs = 1,
        int maxDelayMs = 5,
        int budgetMs = 60_000)
    {
        var decorator = new RetryLlmClientDecorator(NullLoggerFactory.Instance);
        return decorator.Wrap(inner, new ProviderConfig
        {
            Id = "p",
            Type = ProviderTypes.OpenAi,
            MaxRetries = maxRetries,
            RetryBaseDelayMs = baseDelayMs,
            RetryMaxDelayMs = maxDelayMs,
            RetryTotalBudgetMs = budgetMs
        });
    }

    private static EmbeddingRequest Embedding() =>
        new() { Model = "m", Inputs = ["a"] };

    // ────────────────────────── 按端口分派 ──────────────────────────

    [Fact]
    public void Wrap_ShouldPickMatchingRetryingClientPerPort()
    {
        Wrap(new FlakyChatClient([null]))
            .Should().BeAssignableTo<IChatCompletionClient>()
            .And.BeOfType<RetryingChatCompletionClient>();
        Wrap(new FlakyEmbeddingClient([null])).Should().BeOfType<RetryingEmbeddingClient>();
        Wrap(new FlakyRerankClient([null])).Should().BeOfType<RetryingRerankClient>();
        Wrap(new FlakyDecisionClient([null])).Should().BeOfType<RetryingDecisionClient>();
        Wrap(new FlakySynthesisClient([null])).Should().BeOfType<RetryingSpeechSynthesisClient>();
        Wrap(new FlakyTranscriptionClient([null])).Should().BeOfType<RetryingSpeechTranscriptionClient>();
        Wrap(new FlakyImageClient([null])).Should().BeOfType<RetryingImageGenerationClient>();
        Wrap(new FlakyVideoClient([null])).Should().BeOfType<RetryingVideoGenerationClient>();
    }

    [Fact]
    public void Wrap_UnknownPort_ShouldThrowNotSupported()
    {
        // 决议 15 之后只能靠 is 判断端口。匹配不上任何已知端口时必须显式失败——
        // 静默返回 inner 等于让该 Provider 静默失去重试。
        var act = () => Wrap(new IdentityOnlyClient());

        act.Should().Throw<NotSupportedException>().WithMessage("*未实现任何已知能力端口*");
    }

    [Fact]
    public void Wrap_MultiplePorts_ShouldThrowNotSupported()
    {
        // 「每个客户端实例恰好实现一个能力端口」是隐含契约；违反要报错，不能任取一个端口包装。
        var act = () => Wrap(new ChatAndEmbeddingClient());

        act.Should().Throw<NotSupportedException>().WithMessage("*同时实现了*");
    }

    [Fact]
    public void Wrap_AlreadyWrapped_ShouldNotDoubleWrap()
    {
        var once = Wrap(new FlakyEmbeddingClient([null, null]));

        var twice = Wrap(once);

        twice.Should().BeSameAs(once);
    }

    // ────────────────────────── 非流式重试 ──────────────────────────

    [Fact]
    public async Task EmbeddingRetry_ShouldRetryOnTransientFailure()
    {
        var inner = new FlakyEmbeddingClient([Transient(), null]);
        var client = (IEmbeddingClient)Wrap(inner, maxRetries: 3);

        var response = await client.EmbedAsync(
            Embedding(),
            call: null,
            ct: TestContext.Current.CancellationToken);

        inner.Calls.Should().Be(2);
        response.Vectors.Should().ContainSingle().Which.Values.Should().Equal(1f, 2f);
    }

    [Fact]
    public async Task DecisionRetry_ShouldRetryOnTransientFailure()
    {
        var inner = new FlakyDecisionClient([Transient(), null]);
        var client = (IDecisionClient)Wrap(inner, maxRetries: 3);

        var response = await client.EvaluateAsync(
            new DecisionRequest { Model = "jev-1" },
            call: null,
            ct: TestContext.Current.CancellationToken);

        inner.Calls.Should().Be(2);
        response.Answers["q"].Noul.Should().Be(0.9);
    }

    [Fact]
    public async Task ChatRetry_ShouldRetryOnTransientFailure()
    {
        var inner = new FlakyChatClient([Transient(), null]);
        var client = (IChatCompletionClient)Wrap(inner, maxRetries: 3);

        var response = await client.CompleteAsync(new ChatRequest { Model = "m" });

        response.Message.Content.Should().Be("ok");
        inner.Calls.Should().Be(2);
    }

    [Fact]
    public async Task RerankRetry_ShouldRetryOnTransientFailure()
    {
        var inner = new FlakyRerankClient([Transient(), null]);
        var client = (IRerankClient)Wrap(inner, maxRetries: 3);

        await client.RerankAsync(new RerankRequest { Model = "m", Query = "q", Documents = ["d"] });

        inner.Calls.Should().Be(2);
    }

    [Fact]
    public async Task ImageRetry_ShouldRetryOnTransientFailure()
    {
        var inner = new FlakyImageClient([Transient(), null]);
        var client = (IImageGenerationClient)Wrap(inner, maxRetries: 3);

        await client.GenerateAsync(new ImageGenerationRequest { Model = "m", Prompt = "p" });

        inner.Calls.Should().Be(2);
    }

    [Fact]
    public async Task SynthesisRetry_ShouldRetryOnTransientFailure()
    {
        var inner = new FlakySynthesisClient([Transient(), null]);
        var client = (ISpeechSynthesisClient)Wrap(inner, maxRetries: 3);

        var result = await client.SynthesizeAsync(
            new SpeechSynthesisRequest { Model = "tts", Text = "hi" });

        inner.Calls.Should().Be(2);
        result.Audio.Length.Should().Be(2);
    }

    [Fact]
    public async Task TranscriptionRetry_ShouldRetryOnTransientFailure()
    {
        var inner = new FlakyTranscriptionClient([Transient(), null]);
        var client = (ISpeechTranscriptionClient)Wrap(inner, maxRetries: 3);

        var result = await client.TranscribeAsync(new TranscriptionRequest
        {
            Model = "whisper-1",
            Audio = new MemoryStream(new byte[8])
        });

        inner.Calls.Should().Be(2);
        result.Text.Should().Be("text");
    }

    [Fact]
    public async Task VideoSubmitRetry_ShouldRetryOnTransientFailure()
    {
        var inner = new FlakyVideoClient([Transient(), null]);
        var client = (IVideoGenerationClient)Wrap(inner, maxRetries: 3);

        var handle = await client.SubmitAsync(new VideoGenerationRequest { Model = "m", Prompt = "p" });

        inner.Calls.Should().Be(2);
        handle.JobId.Should().Be("job");
    }

    [Fact]
    public async Task VideoCancel_ShouldRetryOnTransientFailure()
    {
        var inner = new FlakyVideoClient([Transient(), null]);
        var client = (IVideoGenerationClient)Wrap(inner, maxRetries: 3);

        await client.CancelAsync(new MediaJobHandle("job", "p"));

        inner.CancelCalls.Should().Be(2);
    }

    [Fact]
    public async Task ModelCatalogPort_ShouldPassThroughWithoutRetry()
    {
        // 目录查询不是业务调用：装饰器只透传到内层，不重试（重试会把一次目录故障放大成 N 次请求）。
        var inner = new FlakyDecisionClient([]);
        var client = (IModelCatalogClient)Wrap(inner, maxRetries: 3);

        var models = await client.ListModelsAsync(TestContext.Current.CancellationToken);

        inner.ListModelsCalls.Should().Be(1);
        models.Should().ContainSingle().Which.Id.Should().Be("jev-1");
    }

    [Fact]
    public async Task Retry_ShouldWriteAttemptItemsIntoCallContext()
    {
        var inner = new FlakyEmbeddingClient([Transient(), null]);
        var client = (IEmbeddingClient)Wrap(inner, maxRetries: 3);
        var call = new LlmCallContext();

        await client.EmbedAsync(Embedding(), call, TestContext.Current.CancellationToken);

        // 四个记账键：每次尝试前写 attempt / willRetry / maxRetries，退避时补 nextDelayMs
        call.Items[LlmRetryPolicy.AttemptItemKey].Should().Be(2);
        call.Items[LlmRetryPolicy.WillRetryItemKey].Should().Be(false);
        call.Items[LlmRetryPolicy.MaxRetriesItemKey].Should().Be(3);
        call.Items[LlmRetryPolicy.NextDelayItemKey].Should().Be(1d);
        inner.ObservedAttempts.Should().Equal(1, 2);
    }

    [Fact]
    public async Task Retry_ShouldNotRetryHttp4xx()
    {
        var inner = new FlakyEmbeddingClient([NonRetryable()]);
        var client = (IEmbeddingClient)Wrap(inner, maxRetries: 3);

        var act = () => client.EmbedAsync(Embedding());

        await act.Should().ThrowAsync<HttpRequestException>();
        inner.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Retry_ShouldStopAtMaxRetries()
    {
        var inner = new FlakyChatClient(Enumerable.Repeat<Exception?>(Transient(), 5));
        var client = (IChatCompletionClient)Wrap(inner, maxRetries: 2);

        var act = () => client.CompleteAsync(new ChatRequest { Model = "m" });

        await act.Should().ThrowAsync<HttpRequestException>();
        inner.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Retry_ShouldStopWhenBudgetExhausted()
    {
        // base=cap=1ms、budget=2ms → 第三次尝试前的等待会超出预算，抛原始异常
        var inner = new FlakyChatClient(Enumerable.Repeat<Exception?>(Transient(), 10));
        var client = (IChatCompletionClient)Wrap(inner, baseDelayMs: 1, maxDelayMs: 1, budgetMs: 2);

        var act = () => client.CompleteAsync(new ChatRequest { Model = "m" });

        await act.Should().ThrowAsync<HttpRequestException>();
        inner.Calls.Should().Be(3);
    }

    [Fact]
    public async Task Retry_NextDelayItem_ShouldRespectCap()
    {
        var inner = new FlakyChatClient([Transient(), null]);
        var client = (IChatCompletionClient)Wrap(inner, baseDelayMs: 1, maxDelayMs: 1);
        var call = new LlmCallContext();

        await client.CompleteAsync(new ChatRequest { Model = "m" }, call);

        call.Items[LlmRetryPolicy.NextDelayItemKey].Should().Be(1d);
    }

    [Fact]
    public async Task Retry_ShouldNotRetryWhenCallerCancels()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var inner = new FlakyChatClient([new OperationCanceledException(cts.Token)]);
        var client = (IChatCompletionClient)Wrap(inner, maxRetries: 3);

        var act = () => client.CompleteAsync(new ChatRequest { Model = "m" }, null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        inner.Calls.Should().Be(1);
    }

    // ────────────────────────── 流式 / 轮询边界 ──────────────────────────

    [Fact]
    public async Task ChatStream_BeforeFirstChunk_ShouldRetry()
    {
        var inner = new FlakyChatClient([Transient(), null]);
        var client = (IChatCompletionClient)Wrap(inner, maxRetries: 3);

        var chunks = new List<StreamUpdate>();
        await foreach (var update in client.CompleteStreamAsync(
            new ChatRequest { Model = "m" },
            call: null,
            ct: TestContext.Current.CancellationToken))
        {
            chunks.Add(update);
        }

        chunks.Should().ContainSingle(c => c.ContentDelta == "ok");
        inner.StreamCalls.Should().Be(2);
    }

    [Fact]
    public async Task ChatStream_AfterFirstChunk_ShouldNotRetry()
    {
        var inner = new FlakyChatClient([Transient()], yieldBeforeFail: true);
        var client = (IChatCompletionClient)Wrap(inner, maxRetries: 3);

        var act = async () =>
        {
            await foreach (var _ in client.CompleteStreamAsync(new ChatRequest { Model = "m" }))
            {
            }
        };

        await act.Should().ThrowAsync<HttpRequestException>();
        inner.StreamCalls.Should().Be(1);
    }

    [Fact]
    public async Task SpeechStream_AfterFirstChunk_ShouldNotRetry()
    {
        var inner = new FlakySynthesisClient([Transient()], yieldBeforeFail: true);
        var client = (ISpeechSynthesisClient)Wrap(inner, maxRetries: 3);

        var act = async () =>
        {
            await foreach (var _ in client.SynthesizeStreamAsync(
                new SpeechSynthesisRequest { Model = "tts", Text = "hi" }))
            {
            }
        };

        await act.Should().ThrowAsync<HttpRequestException>();
        // 已经交付一块音频：重开会让调用方拿到两段拼接起来的坏音频 → 绝不重试
        inner.StreamCalls.Should().Be(1);
    }

    [Fact]
    public async Task SpeechStream_BeforeFirstChunk_ShouldRetry()
    {
        var inner = new FlakySynthesisClient([Transient(), null]);
        var client = (ISpeechSynthesisClient)Wrap(inner, maxRetries: 3);

        var chunks = new List<AudioChunk>();
        await foreach (var chunk in client.SynthesizeStreamAsync(
            new SpeechSynthesisRequest { Model = "tts", Text = "hi" }))
        {
            chunks.Add(chunk);
        }

        inner.StreamCalls.Should().Be(2);
        chunks.Should().ContainSingle();
    }

    [Fact]
    public async Task TranscriptionStream_BeforeFirstSegment_ShouldRetry()
    {
        var inner = new FlakyTranscriptionClient([Transient(), null]);
        var client = (ISpeechTranscriptionClient)Wrap(inner, maxRetries: 3);

        var segments = new List<TranscriptSegment>();
        await foreach (var segment in client.TranscribeStreamAsync(new TranscriptionRequest
        {
            Model = "whisper-1",
            Audio = new MemoryStream(new byte[8])
        }))
        {
            segments.Add(segment);
        }

        inner.StreamCalls.Should().Be(2);
        segments.Should().ContainSingle().Which.Text.Should().Be("text");
    }

    [Fact]
    public async Task VideoPoll_AfterFirstStatus_ShouldNotRetry()
    {
        var inner = new FlakyVideoClient([Transient()], yieldBeforeFail: true);
        var client = (IVideoGenerationClient)Wrap(inner, maxRetries: 3);

        var act = async () =>
        {
            await foreach (var _ in client.PollAsync(
                new MediaJobHandle("job", "p"), TimeSpan.FromMilliseconds(1)))
            {
            }
        };

        await act.Should().ThrowAsync<HttpRequestException>();
        inner.StreamCalls.Should().Be(1);
    }

    [Fact]
    public async Task VideoPoll_BeforeFirstStatus_ShouldRetry()
    {
        var inner = new FlakyVideoClient([Transient(), null]);
        var client = (IVideoGenerationClient)Wrap(inner, maxRetries: 3);

        var statuses = new List<MediaJobStatus>();
        await foreach (var status in client.PollAsync(
            new MediaJobHandle("job", "p"), TimeSpan.FromMilliseconds(1)))
        {
            statuses.Add(status);
        }

        inner.StreamCalls.Should().Be(2);
        statuses.Should().ContainSingle().Which.State.Should().Be(MediaJobState.Succeeded);
    }

    // ────────────────────────── ASR 请求体可重放（决议 17） ──────────────────────────

    [Fact]
    public async Task Transcribe_SeekableAudio_ShouldRewindBeforeRetry()
    {
        var stream = new DisposeTrackingStream(new byte[8]);
        var inner = new FlakyTranscriptionClient([Transient(), null]);
        var client = (ISpeechTranscriptionClient)Wrap(inner, maxRetries: 3);

        var result = await client.TranscribeAsync(
            new TranscriptionRequest { Model = "whisper-1", Audio = stream });

        inner.Calls.Should().Be(2);
        // 每次进入内层都从头开始，避免把「后半段音频」当整段上传
        inner.ObservedAudioPositions.Should().Equal(0L, 0L);
        // 音频流归调用方所有，装饰器不得 Dispose
        stream.Disposed.Should().BeFalse();
        result.Text.Should().Be("text");
    }

    [Fact]
    public async Task Transcribe_NonSeekableAudio_ShouldNotRetry()
    {
        var inner = new FlakyTranscriptionClient([Transient()]);
        var client = (ISpeechTranscriptionClient)Wrap(inner, maxRetries: 3);

        var act = () => client.TranscribeAsync(new TranscriptionRequest
        {
            Model = "whisper-1",
            Audio = new ForwardOnlyStream(8)
        });

        await act.Should().ThrowAsync<HttpRequestException>();
        inner.Calls.Should().Be(1);
    }

    // ────────────────────────── 身份 / 连通性 / 目录 / 释放 转发 ──────────────────────────

    [Fact]
    public void WrappedClient_ShouldKeepIdentity()
    {
        var client = Wrap(new FlakyEmbeddingClient([null]));

        client.ProviderId.Should().Be("p");
        client.ProviderType.Should().Be(ProviderTypes.OpenAi);
    }

    [Fact]
    public async Task TestConnection_ShouldForwardCapabilityAndNotRetry()
    {
        var inner = new FlakyEmbeddingClient([Transient()]);
        var client = Wrap(inner);

        var ok = await client.TestConnectionAsync(
            "m", LlmCapabilityKind.Embedding, cancellationToken: TestContext.Current.CancellationToken);

        ok.Should().BeTrue();
        inner.TestedCapability.Should().Be(LlmCapabilityKind.Embedding);
        // 连接测试的语义是「一次真实探针的结果」，重试会把瞬时故障掩盖成可用
        inner.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Wrap_ShouldStillExposeRemoteModelCatalog()
    {
        var inner = new CatalogEmbeddingClient();

        var client = Wrap(inner);

        // 决议 15 之后 Provider 只看得见包装对象：目录端口必须透传，否则 listModels 静默失效
        client.Should().BeAssignableTo<IModelCatalogClient>();
        var models = await ((IModelCatalogClient)client).ListModelsAsync(TestContext.Current.CancellationToken);

        models.Should().ContainSingle().Which.Id.Should().Be("remote");
        inner.CatalogCalls.Should().Be(1);
    }

    [Fact]
    public async Task Wrap_WithoutCatalog_ShouldReportEmptyCatalog()
    {
        var client = Wrap(new FlakyEmbeddingClient([null]));

        var models = await ((IModelCatalogClient)client).ListModelsAsync(TestContext.Current.CancellationToken);

        models.Should().BeEmpty();
    }

    [Fact]
    public void Dispose_ShouldForwardToInner()
    {
        var inner = new FlakyEmbeddingClient([null]);

        ((IDisposable)Wrap(inner)).Dispose();

        inner.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task DisposeAsync_ShouldForwardToInnerAsyncDisposable()
    {
        var inner = new AsyncDisposableEmbeddingClient();

        await ((IAsyncDisposable)Wrap(inner)).DisposeAsync();

        inner.AsyncDisposed.Should().BeTrue();
    }

    // ────────────────────────── 注册表 ──────────────────────────

    [Fact]
    public void Registry_Apply_ShouldWrapCapabilityClient()
    {
        var registry = new LlmClientDecoratorRegistry();
        registry.Register(new RetryLlmClientDecorator(NullLoggerFactory.Instance));

        var applied = registry.Apply(new FlakyEmbeddingClient([null]), new ProviderConfig
        {
            Id = "p",
            Type = ProviderTypes.OpenAi
        });

        applied.Should().BeOfType<RetryingEmbeddingClient>();
        applied.ProviderId.Should().Be("p");
    }

    // ────────────────────────── 测试替身 ──────────────────────────

    /// <summary>
    /// 脚本化失败与计数：按队列依次决定「本次抛什么」，并记录装饰器写下的 retry.attempt。
    /// <para>八个替身各自实现对应端口（接口实现无法用继承共享），故把机械动作抽成组合件。</para>
    /// </summary>
    private sealed class FlakyScript
    {
        private readonly Queue<Exception?> _script;

        public FlakyScript(IEnumerable<Exception?> script, bool yieldBeforeFail)
        {
            _script = new Queue<Exception?>(script);
            YieldBeforeFail = yieldBeforeFail;
        }

        public int Calls { get; private set; }
        public int StreamCalls { get; private set; }
        public bool YieldBeforeFail { get; }
        public LlmCapabilityKind? TestedCapability { get; private set; }
        public List<int> ObservedAttempts { get; } = new();

        public Exception? NextFailure(LlmCallContext? call, bool stream = false)
        {
            if (stream)
                StreamCalls++;
            else
                Calls++;

            if (call?.Items[LlmRetryPolicy.AttemptItemKey] is int attempt)
                ObservedAttempts.Add(attempt);

            return _script.Dequeue();
        }

        public Task<bool> TestConnection(LlmCapabilityKind capability)
        {
            TestedCapability = capability;
            return Task.FromResult(true);
        }
    }

    private sealed class FlakyChatClient : IChatCompletionClient
    {
        private readonly FlakyScript _script;

        public FlakyChatClient(IEnumerable<Exception?> script, bool yieldBeforeFail = false)
            => _script = new FlakyScript(script, yieldBeforeFail);

        public int Calls => _script.Calls;
        public int StreamCalls => _script.StreamCalls;
        public List<int> ObservedAttempts => _script.ObservedAttempts;
        public string ProviderId => "p";
        public string ProviderType => ProviderTypes.OpenAi;

        public Task<ChatResponse> CompleteAsync(
            ChatRequest request,
            LlmCallContext? call = null,
            CancellationToken ct = default)
        {
            var failure = _script.NextFailure(call);
            if (failure is not null)
                throw failure;

            return Task.FromResult(new ChatResponse
            {
                Id = "r",
                Model = request.Model,
                Message = new ChatMessage { Role = "assistant", Content = "ok" },
                FinishReason = "stop"
            });
        }

        public async IAsyncEnumerable<StreamUpdate> CompleteStreamAsync(
            ChatRequest request,
            LlmCallContext? call = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var failure = _script.NextFailure(call, stream: true);
            if (_script.YieldBeforeFail)
                yield return new StreamUpdate { ContentDelta = "partial" };

            if (failure is not null)
                throw failure;

            yield return new StreamUpdate { ContentDelta = "ok", IsComplete = true };
            await Task.CompletedTask;
        }

        public Task<bool> TestConnectionAsync(
            string modelId,
            LlmCapabilityKind capability,
            LlmCallContext? call = null,
            CancellationToken cancellationToken = default)
            => _script.TestConnection(capability);
    }

    private sealed class FlakyEmbeddingClient : IEmbeddingClient, IDisposable
    {
        private readonly FlakyScript _script;

        public FlakyEmbeddingClient(IEnumerable<Exception?> script) => _script = new FlakyScript(script, false);

        public int Calls => _script.Calls;
        public List<int> ObservedAttempts => _script.ObservedAttempts;
        public LlmCapabilityKind? TestedCapability => _script.TestedCapability;
        public bool Disposed { get; private set; }
        public string ProviderId => "p";
        public string ProviderType => ProviderTypes.OpenAi;

        public Task<EmbeddingResponse> EmbedAsync(
            EmbeddingRequest request,
            LlmCallContext? call = null,
            CancellationToken ct = default)
        {
            var failure = _script.NextFailure(call);
            if (failure is not null)
                throw failure;

            return Task.FromResult(new EmbeddingResponse
            {
                Vectors = [new EmbeddingVector("a", [1f, 2f], 0)]
            });
        }

        public Task<bool> TestConnectionAsync(
            string modelId,
            LlmCapabilityKind capability,
            LlmCallContext? call = null,
            CancellationToken cancellationToken = default)
            => _script.TestConnection(capability);

        public void Dispose() => Disposed = true;
    }

    private sealed class CatalogEmbeddingClient : FlakyEmbeddingClientBase, IModelCatalogClient
    {
        public CatalogEmbeddingClient() : base([null])
        {
        }

        public Task<IReadOnlyList<ModelConfig>> ListModelsAsync(CancellationToken cancellationToken = default)
        {
            CatalogCalls++;
            return Task.FromResult<IReadOnlyList<ModelConfig>>([new ModelConfig { Id = "remote" }]);
        }
    }

    private sealed class AsyncDisposableEmbeddingClient : FlakyEmbeddingClientBase, IAsyncDisposable
    {
        public AsyncDisposableEmbeddingClient() : base([null])
        {
        }

        public bool AsyncDisposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            AsyncDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>embedding 替身的共用体干，供「带远端目录」「带异步释放」两个变体复用。</summary>
    private abstract class FlakyEmbeddingClientBase : IEmbeddingClient
    {
        private readonly FlakyScript _script;

        protected FlakyEmbeddingClientBase(IEnumerable<Exception?> script) => _script = new FlakyScript(script, false);

        public int Calls => _script.Calls;
        public int CatalogCalls { get; protected set; }
        public string ProviderId => "p";
        public string ProviderType => ProviderTypes.OpenAi;

        public Task<EmbeddingResponse> EmbedAsync(
            EmbeddingRequest request,
            LlmCallContext? call = null,
            CancellationToken ct = default)
        {
            var failure = _script.NextFailure(call);
            if (failure is not null)
                throw failure;

            return Task.FromResult(new EmbeddingResponse
            {
                Vectors = [new EmbeddingVector("a", [1f], 0)]
            });
        }

        public Task<bool> TestConnectionAsync(
            string modelId,
            LlmCapabilityKind capability,
            LlmCallContext? call = null,
            CancellationToken cancellationToken = default)
            => _script.TestConnection(capability);
    }

    private sealed class FlakyRerankClient : IRerankClient
    {
        private readonly FlakyScript _script;

        public FlakyRerankClient(IEnumerable<Exception?> script) => _script = new FlakyScript(script, false);

        public int Calls => _script.Calls;
        public string ProviderId => "p";
        public string ProviderType => ProviderTypes.OpenAi;

        public Task<RerankResponse> RerankAsync(
            RerankRequest request,
            LlmCallContext? call = null,
            CancellationToken ct = default)
        {
            var failure = _script.NextFailure(call);
            if (failure is not null)
                throw failure;

            return Task.FromResult(new RerankResponse { Results = [new RerankHit(0, 1.0, "d")] });
        }

        public Task<bool> TestConnectionAsync(
            string modelId,
            LlmCapabilityKind capability,
            LlmCallContext? call = null,
            CancellationToken cancellationToken = default)
            => _script.TestConnection(capability);
    }

    private sealed class FlakyDecisionClient : IDecisionClient, IModelCatalogClient
    {
        private readonly FlakyScript _script;

        public FlakyDecisionClient(IEnumerable<Exception?> script) => _script = new FlakyScript(script, false);

        public int Calls => _script.Calls;
        public int ListModelsCalls { get; private set; }
        public string ProviderId => "p";
        public string ProviderType => ProviderTypes.TypeSafe;

        public Task<DecisionResponse> EvaluateAsync(
            DecisionRequest request,
            LlmCallContext? call = null,
            CancellationToken ct = default)
        {
            var failure = _script.NextFailure(call);
            if (failure is not null)
                throw failure;

            return Task.FromResult(new DecisionResponse
            {
                Model = request.Model,
                Answers = new Dictionary<string, DecisionAnswer>
                {
                    ["q"] = new DecisionAnswer { Type = DecisionQuestionTypes.Noul, Noul = 0.9 }
                }
            });
        }

        public Task<bool> TestConnectionAsync(
            string modelId,
            LlmCapabilityKind capability,
            LlmCallContext? call = null,
            CancellationToken cancellationToken = default)
            => _script.TestConnection(capability);

        /// <summary>目录端口（<see cref="IModelCatalogClient"/>）：装饰器只透传、不重试。</summary>
        async Task<IReadOnlyList<ModelConfig>> IModelCatalogClient.ListModelsAsync(
            CancellationToken cancellationToken)
        {
            ListModelsCalls++;
            await Task.Yield();
            return [new ModelConfig { Id = "jev-1" }];
        }
    }

    private sealed class FlakySynthesisClient : ISpeechSynthesisClient
    {
        private readonly FlakyScript _script;

        public FlakySynthesisClient(IEnumerable<Exception?> script, bool yieldBeforeFail = false)
            => _script = new FlakyScript(script, yieldBeforeFail);

        public int Calls => _script.Calls;
        public int StreamCalls => _script.StreamCalls;
        public string ProviderId => "p";
        public string ProviderType => ProviderTypes.OpenAi;

        public Task<SpeechSynthesisResult> SynthesizeAsync(
            SpeechSynthesisRequest request,
            LlmCallContext? call = null,
            CancellationToken ct = default)
        {
            var failure = _script.NextFailure(call);
            if (failure is not null)
                throw failure;

            return Task.FromResult(new SpeechSynthesisResult(new byte[] { 1, 2 }, "audio/mpeg", null));
        }

        public async IAsyncEnumerable<AudioChunk> SynthesizeStreamAsync(
            SpeechSynthesisRequest request,
            LlmCallContext? call = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var failure = _script.NextFailure(call, stream: true);
            if (_script.YieldBeforeFail)
                yield return new AudioChunk(new byte[] { 1 }, "audio/mpeg");

            if (failure is not null)
                throw failure;

            yield return new AudioChunk(new byte[] { 1, 2 }, "audio/mpeg");
            await Task.CompletedTask;
        }

        public Task<bool> TestConnectionAsync(
            string modelId,
            LlmCapabilityKind capability,
            LlmCallContext? call = null,
            CancellationToken cancellationToken = default)
            => _script.TestConnection(capability);
    }

    private sealed class FlakyTranscriptionClient : ISpeechTranscriptionClient
    {
        private readonly FlakyScript _script;

        public FlakyTranscriptionClient(IEnumerable<Exception?> script, bool yieldBeforeFail = false)
            => _script = new FlakyScript(script, yieldBeforeFail);

        public int Calls => _script.Calls;
        public int StreamCalls => _script.StreamCalls;

        /// <summary>每次进入内层时观察到的音频流位置。</summary>
        public List<long> ObservedAudioPositions { get; } = new();

        public string ProviderId => "p";
        public string ProviderType => ProviderTypes.OpenAi;

        public Task<TranscriptionResult> TranscribeAsync(
            TranscriptionRequest request,
            LlmCallContext? call = null,
            CancellationToken ct = default)
        {
            var failure = Consume(request, call, stream: false);
            if (failure is not null)
                throw failure;

            return Task.FromResult(new TranscriptionResult("text", null, null));
        }

        public async IAsyncEnumerable<TranscriptSegment> TranscribeStreamAsync(
            TranscriptionRequest request,
            LlmCallContext? call = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var failure = Consume(request, call, stream: true);
            if (_script.YieldBeforeFail)
                yield return new TranscriptSegment(TimeSpan.Zero, TimeSpan.FromSeconds(1), "part");

            if (failure is not null)
                throw failure;

            yield return new TranscriptSegment(TimeSpan.Zero, TimeSpan.FromSeconds(1), "text");
            await Task.CompletedTask;
        }

        public Task<bool> TestConnectionAsync(
            string modelId,
            LlmCapabilityKind capability,
            LlmCallContext? call = null,
            CancellationToken cancellationToken = default)
            => _script.TestConnection(capability);

        /// <summary>记录当前位置并「读走一段音频」，以便验证装饰器是否把流回卷。</summary>
        private Exception? Consume(TranscriptionRequest request, LlmCallContext? call, bool stream)
        {
            ObservedAudioPositions.Add(request.Audio.CanSeek ? request.Audio.Position : -1L);
            request.Audio.Read(new byte[4], 0, 4);
            return _script.NextFailure(call, stream);
        }
    }

    private sealed class FlakyImageClient : IImageGenerationClient
    {
        private readonly FlakyScript _script;

        public FlakyImageClient(IEnumerable<Exception?> script) => _script = new FlakyScript(script, false);

        public int Calls => _script.Calls;
        public string ProviderId => "p";
        public string ProviderType => ProviderTypes.OpenAi;

        public Task<ImageGenerationResult> GenerateAsync(
            ImageGenerationRequest request,
            LlmCallContext? call = null,
            CancellationToken ct = default)
        {
            var failure = _script.NextFailure(call);
            if (failure is not null)
                throw failure;

            return Task.FromResult(new ImageGenerationResult(
                [new GeneratedImage("url", null, "image/png", null)], null));
        }

        public Task<bool> TestConnectionAsync(
            string modelId,
            LlmCapabilityKind capability,
            LlmCallContext? call = null,
            CancellationToken cancellationToken = default)
            => _script.TestConnection(capability);
    }

    private sealed class FlakyVideoClient : IVideoGenerationClient
    {
        private readonly FlakyScript _script;
        private readonly Queue<Exception?> _cancelScript;

        public FlakyVideoClient(IEnumerable<Exception?> script, bool yieldBeforeFail = false)
        {
            _script = new FlakyScript(script, yieldBeforeFail);
            _cancelScript = new Queue<Exception?>(script);
        }

        public int Calls => _script.Calls;
        public int StreamCalls => _script.StreamCalls;
        public int CancelCalls { get; private set; }
        public string ProviderId => "p";
        public string ProviderType => ProviderTypes.OpenAi;

        public Task<MediaJobHandle> SubmitAsync(
            VideoGenerationRequest request,
            LlmCallContext? call = null,
            CancellationToken ct = default)
        {
            var failure = _script.NextFailure(call);
            if (failure is not null)
                throw failure;

            return Task.FromResult(new MediaJobHandle("job", "p"));
        }

        public Task<MediaJobStatus> GetStatusAsync(
            MediaJobHandle handle,
            LlmCallContext? call = null,
            CancellationToken ct = default)
        {
            var failure = _script.NextFailure(call);
            if (failure is not null)
                throw failure;

            return Task.FromResult(new MediaJobStatus(MediaJobState.Running, null, null, 0.5));
        }

        public Task CancelAsync(
            MediaJobHandle handle,
            LlmCallContext? call = null,
            CancellationToken ct = default)
        {
            CancelCalls++;
            var failure = _cancelScript.Dequeue();
            if (failure is not null)
                throw failure;

            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<MediaJobStatus> PollAsync(
            MediaJobHandle handle,
            TimeSpan interval,
            LlmCallContext? call = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var failure = _script.NextFailure(call, stream: true);
            if (_script.YieldBeforeFail)
                yield return new MediaJobStatus(MediaJobState.Running, null, null, 0.5);

            if (failure is not null)
                throw failure;

            yield return new MediaJobStatus(MediaJobState.Succeeded, "url", null, 1.0);
            await Task.CompletedTask;
        }

        public Task<bool> TestConnectionAsync(
            string modelId,
            LlmCapabilityKind capability,
            LlmCallContext? call = null,
            CancellationToken cancellationToken = default)
            => _script.TestConnection(capability);
    }

    /// <summary>只带身份、没有任何能力端口的客户端：装饰器应显式拒绝而不是静默放行。</summary>
    private sealed class IdentityOnlyClient : ILlmCapabilityClient
    {
        public string ProviderId => "p";
        public string ProviderType => ProviderTypes.OpenAi;

        public Task<bool> TestConnectionAsync(
            string modelId,
            LlmCapabilityKind capability,
            LlmCallContext? call = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }

    /// <summary>同时实现两个能力端口的客户端：违反「恰好一个端口」的隐含契约。</summary>
    private sealed class ChatAndEmbeddingClient : IChatCompletionClient, IEmbeddingClient
    {
        public string ProviderId => "p";
        public string ProviderType => ProviderTypes.OpenAi;

        public Task<ChatResponse> CompleteAsync(
            ChatRequest request,
            LlmCallContext? call = null,
            CancellationToken ct = default)
            => Task.FromResult(new ChatResponse());

        public IAsyncEnumerable<StreamUpdate> CompleteStreamAsync(
            ChatRequest request,
            LlmCallContext? call = null,
            CancellationToken ct = default)
            => EmptyStream();

        public Task<EmbeddingResponse> EmbedAsync(
            EmbeddingRequest request,
            LlmCallContext? call = null,
            CancellationToken ct = default)
            => Task.FromResult(new EmbeddingResponse { Vectors = [] });

        public Task<bool> TestConnectionAsync(
            string modelId,
            LlmCapabilityKind capability,
            LlmCallContext? call = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        private static async IAsyncEnumerable<StreamUpdate> EmptyStream()
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    /// <summary>可追踪是否被释放的音频流（决议 17 的验证道具）。</summary>
    private sealed class DisposeTrackingStream : MemoryStream
    {
        public DisposeTrackingStream(byte[] buffer) : base(buffer)
        {
        }

        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    /// <summary>只能顺序读的流：读走就无法回卷，装饰器据此放弃重试。</summary>
    private sealed class ForwardOnlyStream : Stream
    {
        private readonly MemoryStream _inner;

        public ForwardOnlyStream(int length) => _inner = new MemoryStream(new byte[length]);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
