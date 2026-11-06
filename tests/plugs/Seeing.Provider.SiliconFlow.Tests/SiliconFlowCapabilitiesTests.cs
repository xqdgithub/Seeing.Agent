using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm.Support.Providers;
using Xunit;

namespace Seeing.Provider.SiliconFlow.Tests;

/// <summary>
/// SiliconFlow 的能力声明：作者默认值（chat + embedding）+ 配置覆盖。
/// <para>背景：旧实现把 <c>Capabilities</c> 硬编码成 <c>Chat</c>，同时 <c>GetClient</c> 里
/// <c>if (capability != Chat) return null</c>。结果是即使端点支持 embeddings，
/// <c>siliconflow/BAAI/bge-large-zh-v1.5</c> 也无法用于 embedding，
/// 且在 Router 第 4 层就被判「Provider 未声明能力」。</para>
/// </summary>
public class SiliconFlowCapabilitiesTests
{
    [Fact]
    public void DefaultCapabilities_ShouldIncludeEmbedding()
    {
        var sut = CreateSut(out _, out _, _ => null);

        sut.Capabilities.Should().HaveFlag(LlmCapabilityKind.Chat);
        sut.Capabilities.Should().HaveFlag(LlmCapabilityKind.Embedding,
            "SiliconFlow 端点提供 OpenAI 兼容的 /v1/embeddings；装完即应可用");
    }

    /// <summary>
    /// 语音转写：SiliconFlow 的 <c>/v1/audio/transcriptions</c> 与我们的
    /// <c>OpenAiSpeechTranscriptionClient</c> 形状一致（已用真端点验证），因此直接声明即可路由。
    /// <para>不声明合成：虽然端点有 <c>/v1/audio/speech</c>，但当前无消费方，声明只是占位。</para>
    /// </summary>
    [Fact]
    public void DefaultCapabilities_ShouldIncludeSpeechTranscriptionButNotSynthesis()
    {
        var sut = CreateSut(out _, out _, _ => null);

        sut.Capabilities.Should().HaveFlag(LlmCapabilityKind.SpeechTranscription,
            "端点提供 OpenAI 兼容的 /v1/audio/transcriptions；装完即可用于语音输入");
        sut.Capabilities.Should().NotHaveFlag(LlmCapabilityKind.SpeechSynthesis,
            "无消费方的能力不声明，避免制造假的「可路由」标记");
    }

    [Fact]
    public void GetClient_Embedding_ShouldCreateEmbeddingClient()
    {
        var embedding = Mock.Of<IEmbeddingClient>();
        var sut = CreateSut(out _, out var requested, capability =>
            capability == LlmCapabilityKind.Embedding ? embedding : null);

        var client = sut.GetClient(LlmCapabilityKind.Embedding);

        client.Should().BeSameAs(embedding);
        requested.Should().Contain(LlmCapabilityKind.Embedding);
    }

    /// <summary>
    /// 每个能力各得一个客户端实例：旧的单 <c>_client</c> 字段在第二个能力上会返回错误类型。
    /// </summary>
    [Fact]
    public void GetClient_ChatAndEmbedding_ShouldReturnDistinctInstances()
    {
        var chat = Mock.Of<IChatCompletionClient>();
        var embedding = Mock.Of<IEmbeddingClient>();
        var sut = CreateSut(out _, out _, capability => capability switch
        {
            LlmCapabilityKind.Chat => chat,
            LlmCapabilityKind.Embedding => embedding,
            _ => null,
        });

        sut.GetClient(LlmCapabilityKind.Chat).Should().BeSameAs(chat);
        sut.GetClient(LlmCapabilityKind.Embedding).Should().BeSameAs(embedding);
    }

    [Fact]
    public async Task ConfiguredCapabilities_ShouldOverrideAuthorDefault()
    {
        var sut = CreateSut(out var store, out _, _ => null);
        await store.SaveAsync(
            new SiliconFlowOptions { ApiKey = "sk", Capabilities = ["chat"] },
            TestContext.Current.CancellationToken);

        await sut.WarmupAsync(TestContext.Current.CancellationToken);

        sut.Capabilities.Should().Be(LlmCapabilityKind.Chat);
        sut.GetClient(LlmCapabilityKind.Embedding).Should().BeNull(
            "配置显式关掉的能力不得回落到作者默认值");
    }

    /// <summary>显式写了非法名字：忽略该项，但**不**把整份配置当成默认值。</summary>
    [Fact]
    public async Task ConfiguredCapabilities_WithUnknownName_ShouldIgnoreAndKeepOthers()
    {
        var sut = CreateSut(out var store, out _, _ => null);
        await store.SaveAsync(
            new SiliconFlowOptions { ApiKey = "sk", Capabilities = ["chat", "not-a-capability"] },
            TestContext.Current.CancellationToken);

        await sut.WarmupAsync(TestContext.Current.CancellationToken);

        sut.Capabilities.Should().Be(LlmCapabilityKind.Chat);
    }

    [Fact]
    public async Task SaveConfigAsync_PersistsCapabilities()
    {
        var sut = CreateSut(out var store, out _, _ => null);

        await sut.SaveConfigAsync(
            new Dictionary<string, object?>
            {
                ["ApiKey"] = "sk-new",
                ["Capabilities"] = new List<string> { "chat", "embedding" },
            },
            Seeing.Agent.Abstractions.Configuration.ConfigLevel.User,
            TestContext.Current.CancellationToken);

        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);
        loaded.ApiKey.Should().Be("sk-new");
        loaded.Capabilities.Should().BeEquivalentTo("chat", "embedding");
        sut.Capabilities.Should().HaveFlag(LlmCapabilityKind.Embedding);
    }

    private static SiliconFlowProvider CreateSut(
        out SiliconFlowConfigStore store,
        out List<LlmCapabilityKind> requested,
        Func<LlmCapabilityKind, ILlmCapabilityClient?> create)
    {
        var dir = Path.Combine(Path.GetTempPath(), "siliconflow-caps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        store = new SiliconFlowConfigStore(dir, NullLogger<SiliconFlowConfigStore>.Instance);

        var calls = new List<LlmCapabilityKind>();
        requested = calls;

        var factory = new Mock<ILlmClientFactory>();
        factory.Setup(f => f.Create(It.IsAny<ProviderConfig>(), It.IsAny<LlmCapabilityKind>()))
            .Returns((ProviderConfig _, LlmCapabilityKind capability) =>
            {
                calls.Add(capability);
                return create(capability);
            });

        var resolver = new LlmClientFactoryResolver(
            [new LlmClientFactoryRegistration(ProviderTypes.OpenAi, factory.Object)]);

        var registry = new Mock<IProviderRegistry>();

        return new SiliconFlowProvider(
            store,
            resolver,
            registry.Object,
            new SiliconFlowModelsClient(NullLogger<SiliconFlowModelsClient>.Instance),
            Mock.Of<IModelCapabilityManager>(),
            NullLogger<SiliconFlowProvider>.Instance);
    }
}
