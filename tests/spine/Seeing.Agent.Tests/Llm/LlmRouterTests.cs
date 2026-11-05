using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Core.Llm;
using Seeing.Agent.Llm;
using Xunit;

namespace Seeing.Agent.Tests.Llm;

/// <summary>
/// Task R-1：LlmRouter 五层校验（spec §7.4）。
/// <para>逐层失败各自抛不同消息；缓存键是 (providerId, capability) 而非 modelId；
/// TryResolve 不抛且给出可读原因（Review Focus #3：Speech 缺 Modalities 必须给"声明不足"）。</para>
/// </summary>
public class LlmRouterTests
{
    private const string ProviderId = "openai";

    /// <summary>模型目录替身：GetModel 命中登记表，GetEffectiveTypes 复刻 Types 空→[Text] 的规则。</summary>
    private static Mock<IModelConfigManager> CreateModels(
        params (string ModelId, ModelConfig Config)[] catalog)
    {
        var byId = catalog.ToDictionary(pair => pair.ModelId, pair => pair.Config, StringComparer.OrdinalIgnoreCase);
        var models = new Mock<IModelConfigManager>();
        models.Setup(m => m.GetModel(It.IsAny<string>()))
            .Returns((string id) => byId.GetValueOrDefault(id));
        models.Setup(m => m.GetEffectiveTypes(It.IsAny<ModelConfig>()))
            .Returns((ModelConfig config) => config.Types is { Count: > 0 } ? config.Types : [ModelType.Text]);
        return models;
    }

    /// <summary>Provider 注册表替身：单 Provider，声明指定能力，按能力供出客户端。</summary>
    private static Mock<IProviderRegistry> CreateProviders(
        LlmCapabilityKind declared,
        Func<LlmCapabilityKind, ILlmCapabilityClient?> resolveClient)
    {
        var provider = new Mock<ILlmProvider>();
        provider.SetupGet(p => p.Id).Returns(ProviderId);
        provider.SetupGet(p => p.Protocol).Returns(ProviderTypes.OpenAi);
        provider.SetupGet(p => p.Capabilities).Returns(declared);
        provider.Setup(p => p.GetClient(It.IsAny<LlmCapabilityKind>()))
            .Returns((LlmCapabilityKind kind) => resolveClient(kind));

        var registry = new Mock<IProviderRegistry>();
        registry.Setup(r => r.GetProvider(ProviderId)).Returns(provider.Object);
        registry.Setup(r => r.GetProvider(It.Is<string>(id => id != ProviderId))).Returns((ILlmProvider?)null);
        return registry;
    }

    private static LlmRouter CreateRouter(
        Mock<IModelConfigManager> models,
        Mock<IProviderRegistry> providers)
        => new(models.Object, providers.Object, NullLogger<LlmRouter>.Instance);

    /// <summary>
    /// 一个客户端类型实现两个能力端口 → 歧义。
    /// <para>决议 15 删掉了客户端自述能力，反查只能靠端口类型；这使「每个客户端恰好实现
    /// 一个端口」成为隐含契约。歧义时必须失败而非任取一个——否则同一个客户端可能被
    /// 当作 chat 也可能被当作 embedding，行为随实现细节漂移。</para>
    /// </summary>
    [Fact]
    public void Resolve_ClientTypeImplementingTwoPorts_ShouldThrowAmbiguous()
    {
        var router = CreateRouter(CreateModels(), CreateProviders(LlmCapabilityKind.Chat, _ => null));

        var act = () => router.Resolve<IChatAndEmbeddingClient>("openai/m");

        var ex = act.Should().Throw<LlmCapabilityMismatchException>().Which;
        ex.Reason.Should().Contain("端口", "歧义要指出反查失败的原因是端口匹配数不为 1");
    }

    private interface IChatAndEmbeddingClient : IChatCompletionClient, IEmbeddingClient
    {
    }

    private static ModelConfig Model(ModelType type, string provider = ProviderId)
        => new()
        {
            Id = "m",
            Provider = provider,
            Types = [type]
        };

    [Fact]
    public void Resolve_UnknownModel_ShouldThrow()
    {
        var router = CreateRouter(CreateModels(), CreateProviders(LlmCapabilityKind.Chat, _ => null));

        var act = () => router.Resolve<IChatCompletionClient>("openai/ghost-model");

        var ex = act.Should().Throw<LlmCapabilityMismatchException>().Which;
        ex.Message.Should().Contain("ghost-model", "消息必须带上被请求的 modelId");
        ex.RequestedCapability.Should().Be(LlmCapabilityKind.Chat);
        ex.Reason.Should().Contain("不存在");
    }

    [Fact]
    public void Resolve_EmbeddingModelAsChat_ShouldThrow()
    {
        var models = CreateModels(("openai/embed-me", Model(ModelType.Embedding)));
        var router = CreateRouter(models, CreateProviders(LlmCapabilityKind.Chat, _ => Mock.Of<IChatCompletionClient>()));

        var act = () => router.Resolve<IChatCompletionClient>("openai/embed-me");

        var ex = act.Should().Throw<LlmCapabilityMismatchException>().Which;
        ex.Message.Should().Contain("模型声明类型", "第 2 层失败必须说明模型声明的类型");
        ex.DeclaredModelTypes.Should().Contain(ModelType.Embedding);
        // 第 3/4/5 层不该被走到
        ex.ProviderCapabilities.Should().BeNull();
    }

    [Fact]
    public void Resolve_SpeechWithoutModalities_ShouldThrowDeclarationInsufficient()
    {
        // Review Focus #3：只标 Speech、不填 Modalities → 给"声明不足"的可操作提示，而不是静默选错
        var speechModel = new ModelConfig { Id = "speech", Provider = ProviderId, Types = [ModelType.Speech] };
        var models = CreateModels(("openai/speech", speechModel));
        var router = CreateRouter(
            models,
            CreateProviders(
                LlmCapabilityKind.SpeechSynthesis | LlmCapabilityKind.SpeechTranscription,
                _ => Mock.Of<ISpeechSynthesisClient>()));

        var act = () => router.Resolve<ISpeechSynthesisClient>("openai/speech");

        var ex = act.Should().Throw<LlmCapabilityMismatchException>().Which;
        ex.Message.Should().Contain("声明不足").And.Contain("Modalities");
        ex.Reason.Should().Contain("modalities.output", "合成看 output");
    }

    [Fact]
    public void Resolve_SpeechTranscriptionWithoutInputAudio_ShouldThrowDeclarationInsufficient()
    {
        var speechModel = new ModelConfig
        {
            Id = "speech",
            Provider = ProviderId,
            Types = [ModelType.Speech],
            Modalities = new ModelModalities { Output = ["audio"] }
        };
        var models = CreateModels(("openai/speech", speechModel));
        var router = CreateRouter(
            models,
            CreateProviders(LlmCapabilityKind.SpeechTranscription, _ => Mock.Of<ISpeechTranscriptionClient>()));

        var act = () => router.Resolve<ISpeechTranscriptionClient>("openai/speech");

        act.Should().Throw<LlmCapabilityMismatchException>()
            .Which.Reason.Should().Contain("modalities.input", "转写看 input");
    }

    [Fact]
    public void Resolve_SpeechWithModalities_ShouldPassLayerThree()
    {
        var ttsModel = new ModelConfig
        {
            Id = "tts",
            Provider = ProviderId,
            Types = [ModelType.Speech],
            Modalities = new ModelModalities { Output = ["audio"] }
        };
        var client = Mock.Of<ISpeechSynthesisClient>();
        var router = CreateRouter(
            CreateModels(("openai/tts", ttsModel)),
            CreateProviders(LlmCapabilityKind.SpeechSynthesis, _ => client));

        router.Resolve<ISpeechSynthesisClient>("openai/tts").Should().BeSameAs(client);
    }

    [Fact]
    public void Resolve_ProviderLacksCapability_ShouldThrow()
    {
        // 模型类型说"这是 embedding 模型"，Provider 却没开 embedding → 第 4 层拦住（缺省只给 Chat 的回归防线）
        var models = CreateModels(("openai/embed-me", Model(ModelType.Embedding)));
        var router = CreateRouter(models, CreateProviders(LlmCapabilityKind.Chat, _ => null));

        var act = () => router.Resolve<IEmbeddingClient>("openai/embed-me");

        var ex = act.Should().Throw<LlmCapabilityMismatchException>().Which;
        ex.Reason.Should().Contain("未声明能力");
        ex.Message.Should().Contain(LlmCapabilityMap.ToConfigKey(LlmCapabilityKind.Embedding));
        ex.ProviderCapabilities.Should().Be(LlmCapabilityKind.Chat);
    }

    [Fact]
    public void Resolve_ConfiguredCapabilityButNoClient_ShouldThrow()
    {
        // 第 5 层（决议 19）：配置声明了能力，工厂返回 null → 报错而非静默 null、也不回退
        var models = CreateModels(("openai/embed-me", Model(ModelType.Embedding)));
        var router = CreateRouter(models, CreateProviders(LlmCapabilityKind.Embedding, _ => null));

        var act = () => router.Resolve<IEmbeddingClient>("openai/embed-me");

        var ex = act.Should().Throw<LlmCapabilityMismatchException>().Which;
        ex.Reason.Should().Contain("工厂未提供对应客户端");
        ex.ProviderCapabilities.Should().Be(LlmCapabilityKind.Embedding);
    }

    [Fact]
    public void Resolve_TwoModelsSameProvider_ShouldReturnSameClientInstance()
    {
        // 缓存键是 providerId（不是 modelId）：同 Provider 的两个模型必须拿到同一个客户端实例
        var chat = Mock.Of<IChatCompletionClient>();
        var models = CreateModels(
            ("openai/a", new ModelConfig { Id = "a", Provider = ProviderId, Types = [ModelType.Text] }),
            ("openai/b", new ModelConfig { Id = "b", Provider = ProviderId, Types = [ModelType.Text] }));
        var providers = CreateProviders(LlmCapabilityKind.Chat, _ => chat);
        var router = CreateRouter(models, providers);

        var first = router.Resolve<IChatCompletionClient>("openai/a");
        var second = router.Resolve<IChatCompletionClient>("openai/b");

        first.Should().BeSameAs(second);
        providers.Verify(r => r.GetProvider(ProviderId), Times.Exactly(2));
    }

    [Fact]
    public void TryResolve_ShouldNotThrowAndGiveReason()
    {
        var models = CreateModels(("openai/embed-me", Model(ModelType.Embedding)));
        var router = CreateRouter(models, CreateProviders(LlmCapabilityKind.Chat, _ => null));

        var ok = router.TryResolve<IEmbeddingClient>("openai/embed-me", out var client, out var reason);

        ok.Should().BeFalse();
        client.Should().BeNull();
        reason.Should().NotBeNullOrWhiteSpace();

        // 空 modelId 也不抛：路由器只接受显式引用
        router.TryResolve<IChatCompletionClient>(" ", out _, out var blankReason).Should().BeFalse();
        blankReason.Should().Contain("显式");
    }

    [Fact]
    public void TryResolve_WhenAllLayersPass_ShouldGiveClient()
    {
        var chat = Mock.Of<IChatCompletionClient>();
        var models = CreateModels(("openai/gpt", Model(ModelType.Text)));
        var router = CreateRouter(models, CreateProviders(LlmCapabilityKind.Chat, kind => kind == LlmCapabilityKind.Chat ? chat : null));

        router.TryResolve<IChatCompletionClient>("openai/gpt", out var client, out var reason).Should().BeTrue();
        client.Should().BeSameAs(chat);
        reason.Should().BeNull();
    }

    [Fact]
    public void Resolve_MultiLabelModel_ShouldUseAnyMatchingType()
    {
        // 一个模型同时标 Text + Embedding：两个端口都应解析成功（多标签语义）
        var multi = new ModelConfig
        {
            Id = "multi",
            Provider = ProviderId,
            Types = [ModelType.Text, ModelType.Embedding]
        };
        var chat = Mock.Of<IChatCompletionClient>();
        var embedding = Mock.Of<IEmbeddingClient>();
        var router = CreateRouter(
            CreateModels(("openai/multi", multi)),
            CreateProviders(
                LlmCapabilityKind.Chat | LlmCapabilityKind.Embedding,
                kind => kind == LlmCapabilityKind.Chat ? chat : kind == LlmCapabilityKind.Embedding ? embedding : null));

        router.Resolve<IChatCompletionClient>("openai/multi").Should().BeSameAs(chat);
        router.Resolve<IEmbeddingClient>("openai/multi").Should().BeSameAs(embedding);
    }

    [Fact]
    public void Resolve_UnknownProvider_ShouldThrowWithProviderId()
    {
        var models = CreateModels(("ghost/gpt", Model(ModelType.Text, provider: "ghost")));
        var router = CreateRouter(models, CreateProviders(LlmCapabilityKind.Chat, _ => Mock.Of<IChatCompletionClient>()));

        var act = () => router.Resolve<IChatCompletionClient>("ghost/gpt");

        act.Should().Throw<LlmCapabilityMismatchException>().Which.Reason.Should().Contain("ghost");
    }

    [Fact]
    public void Resolve_WeirdPort_ShouldThrowWithReadableReason()
    {
        var router = CreateRouter(CreateModels(), CreateProviders(LlmCapabilityKind.Chat, _ => null));

        var act = () => router.Resolve<IRerankClient>("openai/whatever");

        act.Should().Throw<LlmCapabilityMismatchException>().Which.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Resolve_RerankContractOnly_ShouldFailBeforeReachingFactory()
    {
        // §7.11：rerank 只出契约。模型标 Rerank、Provider 未开该能力 → 第 4 层就给出明确异常
        var models = CreateModels(("openai/rerank-me", Model(ModelType.Rerank)));
        var router = CreateRouter(models, CreateProviders(LlmCapabilityKind.Chat, _ => null));

        var act = () => router.Resolve<IRerankClient>("openai/rerank-me");

        act.Should().Throw<LlmCapabilityMismatchException>()
            .Which.Reason.Should().Contain("未声明能力");
    }

    [Fact]
    public void Resolve_BlankProviderOnModel_ShouldThrow()
    {
        var models = CreateModels(("orphan", new ModelConfig { Id = "orphan", Provider = "", Types = [ModelType.Text] }));
        var router = CreateRouter(models, CreateProviders(LlmCapabilityKind.Chat, _ => null));

        var act = () => router.Resolve<IChatCompletionClient>("orphan");

        act.Should().Throw<LlmCapabilityMismatchException>().Which.Reason.Should().Contain("未绑定 Provider");
    }
}
