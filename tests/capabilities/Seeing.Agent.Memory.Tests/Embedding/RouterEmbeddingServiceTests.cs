using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm;
using Seeing.Agent.Memory.Configuration;
using Seeing.Agent.Memory.Core.Embedding;
using Xunit;

namespace Seeing.Agent.Memory.Tests.Embedding;

/// <summary>
/// <see cref="RouterEmbeddingService"/> 的解析与调用契约。
/// <para>核心红线（spec §0 决议 10 / §13 原则 1 / §7.8.1）：模型只来自显式配置的
/// <c>Embedding.ModelId</c>，不读 <c>DefaultModels.embedding</c>、不挑目录第一个。</para>
/// </summary>
public class RouterEmbeddingServiceTests
{
    private const string ModelRef = "openai/text-embedding-3-small";

    private readonly Mock<ILlmInvoker> _invoker = new();
    private readonly Mock<IModelConfigManager> _models = new();
    private readonly Mock<IEmbeddingClient> _client = new();
    private EmbeddingRequest? _capturedRequest;

    public RouterEmbeddingServiceTests()
    {
        _client.Setup(c => c.EmbedAsync(
                It.IsAny<EmbeddingRequest>(),
                It.IsAny<LlmCallContext>(),
                It.IsAny<CancellationToken>()))
            .Callback((EmbeddingRequest req, LlmCallContext call, CancellationToken token) => _capturedRequest = req)
            .ReturnsAsync((EmbeddingRequest req, LlmCallContext call, CancellationToken token) =>
                new EmbeddingResponse
                {
                    Vectors = req.Inputs.Select((t, i) => new EmbeddingVector(t, new[] { 0.1f, 0.2f, 0.3f }, i)).ToList(),
                    Usage = new TokenUsage { InputTokens = 9 },
                });

        _invoker.Setup(i => i.InvokeAsync<IEmbeddingClient, EmbeddingResponse>(
                It.IsAny<string>(),
                It.IsAny<Func<IEmbeddingClient, LlmCallContext, CancellationToken, Task<EmbeddingResponse>>>(),
                It.IsAny<LlmInvocationOptions?>(),
                It.IsAny<CancellationToken>()))
            .Returns((string modelId, Func<IEmbeddingClient, LlmCallContext, CancellationToken, Task<EmbeddingResponse>> call, LlmInvocationOptions? _, CancellationToken ct)
                => call(_client.Object, new LlmCallContext(), ct));
    }

    [Fact]
    public async Task Resolve_UsesExplicitModelIdNotCatalogFirst()
    {
        // Arrange：目录里另有其他 Embedding 模型、全局默认模型也已配置——都不许被采用。
        _models.Setup(m => m.GetModel(ModelRef)).Returns(new ModelConfig { Id = "text-embedding-3-small", Provider = "openai" });
        _models.Setup(m => m.GetDefaultModel(LlmCapabilityKind.Embedding)).Returns("siliconflow/BAAI/bge-m3");
        var sut = Create(enabled: true, modelId: ModelRef);

        // Act
        var results = await sut.EmbedBatchAsync(new[] { "甲", "乙" }, TestContext.Current.CancellationToken);

        // Assert：调用带的 modelId 恰是配置值
        _invoker.Verify(i => i.InvokeAsync<IEmbeddingClient, EmbeddingResponse>(
            ModelRef,
            It.IsAny<Func<IEmbeddingClient, LlmCallContext, CancellationToken, Task<EmbeddingResponse>>>(),
            It.IsAny<LlmInvocationOptions?>(),
            It.IsAny<CancellationToken>()), Times.Once);

        results.Should().HaveCount(2);
        results[0].Text.Should().Be("甲");
        results[1].Text.Should().Be("乙");
        results[0].Vector.Should().HaveCount(3);
        results[0].TokenCount.Should().Be(9);
        sut.Dimensions.Should().Be(3);
        sut.ProviderName.Should().Be("openai");

        // 发给 API 的是目录里的裸 ID，不是带 provider 前缀的引用
        _capturedRequest!.Model.Should().Be("text-embedding-3-small");
        _capturedRequest.Inputs.Should().Equal("甲", "乙");

        // 启发式选择一律没发生
        _models.Verify(m => m.GetModels(), Times.Never);
        _models.Verify(m => m.GetModelsByType(It.IsAny<ModelType>(), It.IsAny<string?>()), Times.Never);
        _models.Verify(m => m.GetDefaultModel(It.IsAny<LlmCapabilityKind>()), Times.Never,
            "Memory 不读 DefaultModels.embedding（决议 10）");
    }

    [Fact]
    public async Task EnabledFalse_ShouldThrowWithoutCallingInvoker()
    {
        var sut = Create(enabled: false, modelId: ModelRef);

        var act = () => sut.EmbedAsync("文本", TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*Embedding.Enabled*");
        _invoker.Invocations.Should().BeEmpty("未启用时不得发出任何向量调用（不自动开跑）");
    }

    [Fact]
    public async Task EnabledTrueButNoModelId_ShouldThrowAndNotGuessModel()
    {
        _models.Setup(m => m.GetDefaultModel(LlmCapabilityKind.Embedding)).Returns(ModelRef);
        var sut = Create(enabled: true, modelId: null);

        var act = () => sut.EmbedAsync("文本", TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*ModelId*");
        _invoker.Invocations.Should().BeEmpty();
        _models.Verify(m => m.GetDefaultModel(It.IsAny<LlmCapabilityKind>()), Times.Never);
    }

    [Fact]
    public async Task CapabilityMismatch_ShouldSurfaceModelIdAndReason()
    {
        _models.Setup(m => m.GetModel(ModelRef)).Returns(new ModelConfig { Id = "text-embedding-3-small" });
        _invoker.Setup(i => i.InvokeAsync<IEmbeddingClient, EmbeddingResponse>(
                It.IsAny<string>(),
                It.IsAny<Func<IEmbeddingClient, LlmCallContext, CancellationToken, Task<EmbeddingResponse>>>(),
                It.IsAny<LlmInvocationOptions?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new LlmCapabilityMismatchException(
                ModelRef, LlmCapabilityKind.Embedding, "Provider 'openai' 未声明该能力"));

        var sut = Create(enabled: true, modelId: ModelRef);

        var act = () => sut.EmbedAsync("文本", TestContext.Current.CancellationToken);

        var failure = await act.Should().ThrowAsync<LlmCapabilityMismatchException>();
        failure.WithMessage($"*{ModelRef}*未声明该能力*");
        failure.Which.ModelId.Should().Be(ModelRef);
        failure.Which.Reason.Should().Contain("openai");
    }

    [Fact]
    public async Task EmptyVectors_ShouldThrowExplicitly()
    {
        _models.Setup(m => m.GetModel(ModelRef)).Returns(new ModelConfig { Id = "text-embedding-3-small" });
        _client.Setup(c => c.EmbedAsync(
                It.IsAny<EmbeddingRequest>(),
                It.IsAny<LlmCallContext?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EmbeddingResponse { Vectors = Array.Empty<EmbeddingVector>() });

        var sut = Create(enabled: true, modelId: ModelRef);

        var act = () => sut.EmbedBatchAsync(new[] { "文本" }, TestContext.Current.CancellationToken);

        var empty = await act.Should().ThrowAsync<InvalidOperationException>();
        empty.WithMessage("*空响应*");
    }

    [Fact]
    public void ProviderName_WhenUnconfigured_ShouldSayUnconfigured()
    {
        Create(enabled: false, modelId: null).ProviderName.Should().Be("unconfigured");
    }

    [Fact]
    public async Task EmbedAsync_SingleText_ShouldDelegateToBatch()
    {
        _models.Setup(m => m.GetModel(ModelRef)).Returns(new ModelConfig { Id = "text-embedding-3-small" });
        var sut = Create(enabled: true, modelId: ModelRef);

        var result = await sut.EmbedAsync("单条文本", TestContext.Current.CancellationToken);

        result.Text.Should().Be("单条文本");
        result.Vector.Should().HaveCount(3);
    }

    private RouterEmbeddingService Create(bool enabled, string? modelId) =>
        new(
            _invoker.Object,
            _models.Object,
            new StaticMonitor(new MemoryOptions
            {
                Embedding = new MemoryEmbeddingOptions { Enabled = enabled, ModelId = modelId }
            }),
            NullLogger<RouterEmbeddingService>.Instance);

    private sealed class StaticMonitor : IOptionsMonitor<MemoryOptions>
    {
        public StaticMonitor(MemoryOptions value) => CurrentValue = value;
        public MemoryOptions CurrentValue { get; }
        public MemoryOptions Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<MemoryOptions, string?> listener) => new Nop();
        private sealed class Nop : IDisposable { public void Dispose() { } }
    }
}
