using FluentAssertions;
using Moq;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm;
using Seeing.Agent.Memory.Configuration;
using Seeing.Agent.Memory.Core.Embedding;
using Xunit;

namespace Seeing.Agent.Memory.Tests.Embedding;

/// <summary>
/// 连接测试探针：必须经 <see cref="ILlmInvoker"/> 发一次真实单条向量调用（决议 25），
/// 旧的裸 HTTP + <c>IProviderEndpointLookup</c> 路径已删除。
/// </summary>
public class EmbeddingConnectionTesterTests
{
    private const string ModelRef = "openai/text-embedding-3-small";

    private readonly Mock<ILlmInvoker> _invoker = new();
    private readonly Mock<IModelConfigManager> _models = new();
    private readonly Mock<IEmbeddingClient> _client = new();

    public EmbeddingConnectionTesterTests()
    {
        _client.Setup(c => c.EmbedAsync(
                It.IsAny<EmbeddingRequest>(),
                It.IsAny<LlmCallContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EmbeddingResponse
            {
                Vectors = new[] { new EmbeddingVector("probe", new float[] { 0.1f, 0.2f, 0.3f }, 0) },
            });

        _invoker.Setup(i => i.InvokeAsync<IEmbeddingClient, EmbeddingResponse>(
                It.IsAny<string>(),
                It.IsAny<Func<IEmbeddingClient, LlmCallContext, CancellationToken, Task<EmbeddingResponse>>>(),
                It.IsAny<LlmInvocationOptions?>(),
                It.IsAny<CancellationToken>()))
            .Returns((string modelId, Func<IEmbeddingClient, LlmCallContext, CancellationToken, Task<EmbeddingResponse>> call, LlmInvocationOptions? opts, CancellationToken ct)
                => call(_client.Object, new LlmCallContext(), ct));
    }

    /// <summary>
    /// 连接测试是「点一下按钮验证连通」的探针，不得把一次人工验证污染成业务调用观测。
    /// <para>它可走 Invoker（复用五层校验），但必须 <c>EmitHooks = false</c>，
    /// 否则会触发 <c>llm.params</c>/<c>llm.after_complete</c>（spec §7.4.1）。</para>
    /// </summary>
    [Fact]
    public async Task TestAsync_ShouldNotEmitLlmHooks()
    {
        LlmInvocationOptions? captured = null;
        _invoker
            .Setup(i => i.InvokeAsync<IEmbeddingClient, EmbeddingResponse>(
                It.IsAny<string>(),
                It.IsAny<Func<IEmbeddingClient, LlmCallContext, CancellationToken, Task<EmbeddingResponse>>>(),
                It.IsAny<LlmInvocationOptions?>(),
                It.IsAny<CancellationToken>()))
            .Callback((string _, Func<IEmbeddingClient, LlmCallContext, CancellationToken, Task<EmbeddingResponse>> call,
                LlmInvocationOptions? opts, CancellationToken ct) => captured = opts)
            .Returns((string _, Func<IEmbeddingClient, LlmCallContext, CancellationToken, Task<EmbeddingResponse>> call,
                LlmInvocationOptions? opts, CancellationToken ct)
                => call(_client.Object, new LlmCallContext(), ct));

        var sut = CreateTester(new MemoryOptions
        {
            Embedding = new MemoryEmbeddingOptions { Enabled = true, ModelId = "openai/text-embedding-3-small" }
        });

        await sut.TestAsync(null, TestContext.Current.CancellationToken);

        captured.Should().NotBeNull();
        captured!.EmitHooks.Should().BeFalse(
            "探针不得触发 llm.* Hook——否则「测试连接」会污染预算账与审计日志");
    }

    [Fact]
    public async Task TestAsync_WhenNoModelIdAnywhere_ShouldFailWithoutCallingInvoker()
    {        var sut = CreateTester(new MemoryOptions());

        var result = await sut.TestAsync(null, TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("ModelId");
        _invoker.Invocations.Should().BeEmpty("没模型就不探针，绝不自动挑一个");
    }

    [Fact]
    public async Task TestAsync_ShouldProbeOnceThroughInvoker_AndReportDimensions()
    {
        _models.Setup(m => m.GetModel(ModelRef)).Returns(new ModelConfig { Id = "text-embedding-3-small" });
        var sut = CreateTester(new MemoryOptions
        {
            Embedding = new MemoryEmbeddingOptions { Enabled = true, ModelId = ModelRef }
        });

        var result = await sut.TestAsync(null, TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        result.Dimensions.Should().Be(3);
        _invoker.Verify(i => i.InvokeAsync<IEmbeddingClient, EmbeddingResponse>(
            ModelRef,
            It.IsAny<Func<IEmbeddingClient, LlmCallContext, CancellationToken, Task<EmbeddingResponse>>>(),
            It.IsAny<LlmInvocationOptions?>(),
            It.IsAny<CancellationToken>()), Times.Once);

        // 只发一条探针文本，且用目录里的裸 ID 打 API
        _client.Verify(c => c.EmbedAsync(
            It.Is<EmbeddingRequest>(r => r.Model == "text-embedding-3-small" && r.Inputs.Count == 1),
            It.IsAny<LlmCallContext>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TestAsync_WithExplicitModelId_ShouldProbeThatModel()
    {
        // 配置未启用时 UI 也能测：入参显式引用的模型优先，与 Enabled 无关。
        _models.Setup(m => m.GetModel("siliconflow/BAAI/bge-m3"))
            .Returns(new ModelConfig { Id = "BAAI/bge-m3" });
        var sut = CreateTester(new MemoryOptions());

        var result = await sut.TestAsync("siliconflow/BAAI/bge-m3", TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        _invoker.Verify(i => i.InvokeAsync<IEmbeddingClient, EmbeddingResponse>(
            "siliconflow/BAAI/bge-m3",
            It.IsAny<Func<IEmbeddingClient, LlmCallContext, CancellationToken, Task<EmbeddingResponse>>>(),
            It.IsAny<LlmInvocationOptions?>(),
            It.IsAny<CancellationToken>()), Times.Once);
        _client.Verify(c => c.EmbedAsync(
            It.Is<EmbeddingRequest>(r => r.Model == "BAAI/bge-m3"),
            It.IsAny<LlmCallContext>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TestAsync_WhenModelUnresolvable_ShouldReportFailureNotThrow()
    {
        _invoker.Setup(i => i.InvokeAsync<IEmbeddingClient, EmbeddingResponse>(
                It.IsAny<string>(),
                It.IsAny<Func<IEmbeddingClient, LlmCallContext, CancellationToken, Task<EmbeddingResponse>>>(),
                It.IsAny<LlmInvocationOptions?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new LlmCapabilityMismatchException(ModelRef, LlmCapabilityKind.Embedding, "模型不存在"));

        var sut = CreateTester(new MemoryOptions
        {
            Embedding = new MemoryEmbeddingOptions { Enabled = true, ModelId = ModelRef }
        });

        var result = await sut.TestAsync(null, TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain(ModelRef);
    }

    [Fact]
    public async Task TestAsync_WhenResponseHasNoVector_ShouldFail()
    {
        _models.Setup(m => m.GetModel(ModelRef)).Returns(new ModelConfig { Id = "text-embedding-3-small" });
        _client.Setup(c => c.EmbedAsync(
                It.IsAny<EmbeddingRequest>(),
                It.IsAny<LlmCallContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EmbeddingResponse { Vectors = Array.Empty<EmbeddingVector>() });

        var sut = CreateTester(new MemoryOptions
        {
            Embedding = new MemoryEmbeddingOptions { Enabled = true, ModelId = ModelRef }
        });

        var result = await sut.TestAsync(null, TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("未返回向量");
    }

    [Fact]
    public async Task TestAsync_WhenProbeCancelled_ShouldRethrowNotReportFailure()
    {
        _models.Setup(m => m.GetModel(ModelRef)).Returns(new ModelConfig { Id = "text-embedding-3-small" });
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _invoker.Setup(i => i.InvokeAsync<IEmbeddingClient, EmbeddingResponse>(
                It.IsAny<string>(),
                It.IsAny<Func<IEmbeddingClient, LlmCallContext, CancellationToken, Task<EmbeddingResponse>>>(),
                It.IsAny<LlmInvocationOptions?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        var sut = CreateTester(new MemoryOptions
        {
            Embedding = new MemoryEmbeddingOptions { Enabled = true, ModelId = ModelRef }
        });

        // 取消不是「连接失败」：必须原样上抛，由调用方（UI）判定。
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => sut.TestAsync(null, cts.Token));
    }

    private EmbeddingConnectionTester CreateTester(MemoryOptions options) =>
        new(_invoker.Object, _models.Object, MemoryTestOptions.Monitor(options),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<EmbeddingConnectionTester>.Instance);
}
