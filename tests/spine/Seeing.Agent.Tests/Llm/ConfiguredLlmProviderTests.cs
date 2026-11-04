using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Core.Llm;
using Xunit;

namespace Seeing.Agent.Tests.Llm;

/// <summary>
/// Task R-3：<see cref="ConfiguredLlmProvider"/> 的多能力化。
/// <para>关键契约：未声明能力 <c>GetClient</c> 返回 null（不抛，Review Focus #1）；
/// 同能力重复取回同一实例（缓存键 providerId + capability）；<c>DisposeAsync</c> 释放<b>全部</b>已创建客户端；
/// <c>ListModels</c> 为 true 且客户端实现 <see cref="IModelCatalogClient"/> 时合并远端目录。</para>
/// </summary>
public class ConfiguredLlmProviderTests
{
    private static ProviderConfig Config(
        List<string>? capabilities = null,
        bool listModels = false,
        Dictionary<string, ModelConfig>? models = null)
        => new()
        {
            Id = "configured",
            Type = ProviderTypes.OpenAi,
            Name = "Configured",
            Capabilities = capabilities,
            ListModels = listModels,
            Models = models
        };

    /// <summary>按能力分派客户端的工厂替身；未列出的能力返回 null（与真实工厂一致）。</summary>
    private static Mock<ILlmClientFactory> CreateFactory(
        IReadOnlyDictionary<LlmCapabilityKind, ILlmCapabilityClient>? byCapability = null,
        ILlmCapabilityClient? fallback = null)
    {
        var factory = new Mock<ILlmClientFactory>();
        factory.SetupGet(f => f.SupportedProtocols)
            .Returns(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ProviderTypes.OpenAi });
        factory.Setup(f => f.SupportsProtocol(It.IsAny<string>()))
            .Returns((string protocol) => string.Equals(protocol, ProviderTypes.OpenAi, StringComparison.OrdinalIgnoreCase));
        factory.Setup(f => f.Create(It.IsAny<ProviderConfig>(), It.IsAny<LlmCapabilityKind>()))
            .Returns((ProviderConfig _, LlmCapabilityKind kind) =>
                byCapability is not null && byCapability.TryGetValue(kind, out var hit)
                    ? hit
                    : fallback);
        return factory;
    }

    private static ConfiguredLlmProvider CreateProvider(
        ILlmClientFactory factory,
        ProviderConfig? config = null,
        Func<ProviderConfig, ConfigLevel, CancellationToken, Task>? saveAsync = null,
        IModelCapabilityManager? capabilityManager = null)
        => new(
            config ?? Config(),
            factory,
            NullLogger.Instance,
            saveAsync ?? ((_, _, _) => Task.CompletedTask),
            capabilityManager ?? NullModelCapabilityManager.Instance);

    [Fact]
    public void GetClient_UndeclaredCapability_ShouldReturnNull()
    {
        // Review Focus #1：老配置没有 capabilities → 缺省只有 Chat；取 embedding 得 null 而不是抛
        var factory = CreateFactory(fallback: Mock.Of<ILlmCapabilityClient>());
        var sut = CreateProvider(factory.Object);

        sut.Capabilities.Should().Be(LlmCapabilityKind.Chat);
        sut.GetClient(LlmCapabilityKind.Embedding).Should().BeNull();
        factory.Verify(
            f => f.Create(It.IsAny<ProviderConfig>(), LlmCapabilityKind.Embedding),
            Times.Never,
            "未声明的能力不该惊动工厂");
    }

    [Fact]
    public void GetClient_DeclaredCapability_ShouldReturnSameInstanceOnRepeat()
    {
        var chat = Mock.Of<IChatCompletionClient>();
        var embedding = Mock.Of<IEmbeddingClient>();
        var factory = CreateFactory(new Dictionary<LlmCapabilityKind, ILlmCapabilityClient>
        {
            [LlmCapabilityKind.Chat] = chat,
            [LlmCapabilityKind.Embedding] = embedding
        });
        var sut = CreateProvider(factory.Object, Config(capabilities: ["chat", "embedding"]));

        sut.GetClient(LlmCapabilityKind.Chat).Should().BeSameAs(chat);
        sut.GetClient(LlmCapabilityKind.Chat).Should().BeSameAs(chat);
        sut.GetClient(LlmCapabilityKind.Embedding).Should().BeSameAs(embedding);

        factory.Verify(f => f.Create(It.IsAny<ProviderConfig>(), LlmCapabilityKind.Chat), Times.Once);
        factory.Verify(f => f.Create(It.IsAny<ProviderConfig>(), LlmCapabilityKind.Embedding), Times.Once);
    }

    [Fact]
    public void Capabilities_ShouldProjectConfiguredList_AndProtocolDefaultForTypeSafe()
    {
        var sut = CreateProvider(
            CreateFactory().Object,
            Config(capabilities: ["chat", "embedding", "speechSynthesis"]));
        sut.Capabilities.Should().Be(
            LlmCapabilityKind.Chat | LlmCapabilityKind.Embedding | LlmCapabilityKind.SpeechSynthesis);

        var typesafe = CreateProvider(
            CreateFactory().Object,
            new ProviderConfig { Id = "typesafe", Type = ProviderTypes.TypeSafe });
        typesafe.Capabilities.Should().Be(LlmCapabilityKind.Decision);
        typesafe.Protocol.Should().Be(ProviderTypes.TypeSafe);
    }

    [Fact]
    public void GetClient_UnknownCapabilityNameInConfig_ShouldThrowAtConstruction()
    {
        // 决议 19：能力名拼错必须当场抛，不能静默变成一个没有该能力的 Provider
        var act = () => CreateProvider(
            CreateFactory().Object,
            Config(capabilities: ["chat", "embeding"]));

        act.Should().Throw<ArgumentException>().WithMessage("*embeding*");
    }

    [Fact]
    public async Task Dispose_ShouldDisposeEveryCreatedClient()
    {
        var chat = new DisposableClient();
        var embedding = new DisposableClient();
        var decision = new DisposableClient();
        var factory = CreateFactory(new Dictionary<LlmCapabilityKind, ILlmCapabilityClient>
        {
            [LlmCapabilityKind.Chat] = chat,
            [LlmCapabilityKind.Embedding] = embedding,
            [LlmCapabilityKind.Decision] = decision
        });
        var sut = CreateProvider(
            factory.Object,
            Config(capabilities: ["chat", "embedding", "decision"]));

        _ = sut.GetClient(LlmCapabilityKind.Chat);
        _ = sut.GetClient(LlmCapabilityKind.Embedding);
        // decision 从不取用 → 未创建，不该被无谓释放

        await sut.DisposeAsync();

        chat.Disposed.Should().BeTrue();
        embedding.Disposed.Should().BeTrue();
        decision.Disposed.Should().BeFalse("未创建的延迟客户端无需释放");
    }

    [Fact]
    public async Task DisposeAsync_ShouldDisposeAll_WhenEveryClientCreated()
    {
        var chat = new DisposableClient();
        var embedding = new DisposableClient();
        var factory = CreateFactory(new Dictionary<LlmCapabilityKind, ILlmCapabilityClient>
        {
            [LlmCapabilityKind.Chat] = chat,
            [LlmCapabilityKind.Embedding] = embedding
        });
        var sut = CreateProvider(factory.Object, Config(capabilities: ["chat", "embedding"]));

        _ = sut.GetClient(LlmCapabilityKind.Chat);
        _ = sut.GetClient(LlmCapabilityKind.Embedding);
        await sut.DisposeAsync();
        await sut.DisposeAsync();

        chat.Disposed.Should().BeTrue();
        embedding.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task GetModelsAsync_ListModelsTrue_ShouldUnionRemoteCatalog()
    {
        var staticModel = new ModelConfig { Id = "static-one", Name = "静态" };
        var config = Config(
            capabilities: ["chat"],
            listModels: true,
            models: new Dictionary<string, ModelConfig> { ["static-one"] = staticModel });

        var catalogClient = new CatalogChatClient([
            new ModelConfig { Id = "remote-a" },
            new ModelConfig { Id = "remote-b" },
            new ModelConfig { Id = "static-one", Name = "远端同名应被静态压住" }
        ]);
        var factory = CreateFactory(new Dictionary<LlmCapabilityKind, ILlmCapabilityClient>
        {
            [LlmCapabilityKind.Chat] = catalogClient
        });
        var sut = CreateProvider(factory.Object, config);

        var models = await sut.GetModelsAsync(TestContext.Current.CancellationToken);

        models.Select(m => m.Id).Should().BeEquivalentTo("static-one", "remote-a", "remote-b");
        models.First(m => m.Id == "static-one").Name.Should().Be("静态");
        models.Should().OnlyContain(m => m.Provider == "configured");
    }

    [Fact]
    public async Task GetModelsAsync_ListModelsFalse_ShouldIgnoreRemoteCatalog()
    {
        var config = Config(
            capabilities: ["chat"],
            listModels: false,
            models: new Dictionary<string, ModelConfig> { ["static-one"] = new() { Id = "static-one" } });
        var catalogClient = new CatalogChatClient([new ModelConfig { Id = "remote-a" }]);
        var factory = CreateFactory(new Dictionary<LlmCapabilityKind, ILlmCapabilityClient>
        {
            [LlmCapabilityKind.Chat] = catalogClient
        });
        var sut = CreateProvider(factory.Object, config);

        var models = await sut.GetModelsAsync(TestContext.Current.CancellationToken);

        models.Select(m => m.Id).Should().BeEquivalentTo("static-one");
    }

    [Fact]
    public async Task GetModelsAsync_ListModelsTrue_ButClientHasNoCatalogPort_ShouldKeepStaticOnly()
    {
        var config = Config(
            capabilities: ["chat"],
            listModels: true,
            models: new Dictionary<string, ModelConfig> { ["static-one"] = new() { Id = "static-one" } });
        var sut = CreateProvider(
            CreateFactory(fallback: Mock.Of<IChatCompletionClient>()).Object,
            config);

        var models = await sut.GetModelsAsync(TestContext.Current.CancellationToken);

        models.Select(m => m.Id).Should().BeEquivalentTo("static-one");
    }

    [Fact]
    public async Task GetModelsAsync_RemoteCatalogFails_ShouldKeepStaticModels()
    {
        var config = Config(
            capabilities: ["chat"],
            listModels: true,
            models: new Dictionary<string, ModelConfig> { ["static-one"] = new() { Id = "static-one" } });
        var sut = CreateProvider(
            CreateFactory(new Dictionary<LlmCapabilityKind, ILlmCapabilityClient>
            {
                [LlmCapabilityKind.Chat] = new ThrowingCatalogChatClient()
            }).Object,
            config);

        var models = await sut.GetModelsAsync(TestContext.Current.CancellationToken);

        models.Select(m => m.Id).Should().BeEquivalentTo(new[] { "static-one" }, "远端不可用不该让整张目录消失");
    }

    [Fact]
    public async Task LoadConfigAsync_ShouldExposeCapabilitiesAndListModels_ButNoDefaultModel()
    {
        var sut = CreateProvider(
            CreateFactory().Object,
            Config(capabilities: ["chat", "embedding"], listModels: true));

        var values = await ((IConfigurableLlmProvider)sut).LoadConfigAsync(TestContext.Current.CancellationToken);

        values.Keys.Should().NotContain("Models");
        values.Keys.Should().NotContain("DefaultModel");
        values["Capabilities"].Should().BeEquivalentTo(new[] { "chat", "embedding" });
        values["ListModels"].Should().Be(true);
    }

    [Fact]
    public void GetClient_FactoryReturnsNull_ShouldReturnNullForRouterLayerFive()
    {
        // 第 5 层的原料：配置声明了 embedding，工厂给不出客户端 → Provider 返回 null（报错由 Router 负责）
        var factory = CreateFactory(new Dictionary<LlmCapabilityKind, ILlmCapabilityClient>
        {
            [LlmCapabilityKind.Chat] = Mock.Of<IChatCompletionClient>()
        });
        var sut = CreateProvider(factory.Object, Config(capabilities: ["chat", "embedding"]));

        sut.GetClient(LlmCapabilityKind.Embedding).Should().BeNull();
    }

    [Fact]
    public async Task TestConnectionAsync_ShouldDelegateWithCapability_AndFalseWhenNotDeclared()
    {
        var client = new Mock<IEmbeddingClient>();
        client.Setup(c => c.TestConnectionAsync(
                "m", LlmCapabilityKind.Embedding, It.IsAny<LlmCallContext?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var factory = CreateFactory(new Dictionary<LlmCapabilityKind, ILlmCapabilityClient>
        {
            [LlmCapabilityKind.Embedding] = client.Object
        });
        var sut = CreateProvider(factory.Object, Config(capabilities: ["embedding"]));

        (await sut.TestConnectionAsync(
            "m", LlmCapabilityKind.Embedding, TestContext.Current.CancellationToken)).Should().BeTrue();
        (await sut.TestConnectionAsync(
            "m", LlmCapabilityKind.Chat, TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    [Fact]
    public async Task SaveConfigAsync_ShouldPersistCapabilitiesAndListModels()
    {
        ProviderConfig? saved = null;
        var sut = CreateProvider(
            CreateFactory().Object,
            Config(capabilities: ["chat"]),
            saveAsync: (cfg, _, _) =>
            {
                saved = cfg;
                return Task.CompletedTask;
            });

        await ((IConfigurableLlmProvider)sut).SaveConfigAsync(
            new Dictionary<string, object?>
            {
                ["Name"] = "Renamed",
                ["Capabilities"] = new List<string> { "Chat", "Embedding" },
                ["ListModels"] = true
            },
            ConfigLevel.User,
            TestContext.Current.CancellationToken);

        saved.Should().NotBeNull();
        saved!.Name.Should().Be("Renamed");
        saved.Capabilities.Should().BeEquivalentTo(new[] { "chat", "embedding" }, "写盘前规范化为配置键（小驼峰）");
        saved.ListModels.Should().BeTrue();
    }

    [Fact]
    public async Task SaveConfigAsync_ShouldAcceptCommaSeparatedCapabilities()
    {
        ProviderConfig? saved = null;
        var sut = CreateProvider(
            CreateFactory().Object,
            Config(),
            saveAsync: (cfg, _, _) =>
            {
                saved = cfg;
                return Task.CompletedTask;
            });

        await ((IConfigurableLlmProvider)sut).SaveConfigAsync(
            new Dictionary<string, object?> { ["Capabilities"] = "chat,speechSynthesis" },
            ConfigLevel.User,
            TestContext.Current.CancellationToken);

        saved!.Capabilities.Should().BeEquivalentTo(new[] { "chat", "speechSynthesis" });
    }

    [Fact]
    public async Task SaveConfigAsync_ShouldRejectUnknownCapabilityName()
    {
        var sut = CreateProvider(CreateFactory().Object, Config());

        var act = () => ((IConfigurableLlmProvider)sut).SaveConfigAsync(
            new Dictionary<string, object?> { ["Capabilities"] = new List<string> { "chat", "telepathy" } },
            ConfigLevel.User,
            TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.ParamName.Should().Be("Capabilities");
    }

    private sealed class DisposableClient : ILlmCapabilityClient, IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public string ProviderId => "configured";

        public string ProviderType => ProviderTypes.OpenAi;

        public Task<bool> TestConnectionAsync(
            string modelId,
            LlmCapabilityKind capability,
            LlmCallContext? call = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CatalogChatClient(ModelConfig[] remote) : IChatCompletionClient, IModelCatalogClient
    {
        public string ProviderId => "configured";

        public string ProviderType => ProviderTypes.OpenAi;

        public Task<ChatResponse> CompleteAsync(
            ChatRequest request,
            LlmCallContext? call = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<StreamUpdate> CompleteStreamAsync(
            ChatRequest request,
            LlmCallContext? call = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> TestConnectionAsync(
            string modelId,
            LlmCapabilityKind capability,
            LlmCallContext? call = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<IReadOnlyList<ModelConfig>> ListModelsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ModelConfig>>(remote);
    }

    private sealed class ThrowingCatalogChatClient : IChatCompletionClient, IModelCatalogClient
    {
        public string ProviderId => "configured";

        public string ProviderType => ProviderTypes.OpenAi;

        public Task<ChatResponse> CompleteAsync(
            ChatRequest request,
            LlmCallContext? call = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<StreamUpdate> CompleteStreamAsync(
            ChatRequest request,
            LlmCallContext? call = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> TestConnectionAsync(
            string modelId,
            LlmCapabilityKind capability,
            LlmCallContext? call = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<IReadOnlyList<ModelConfig>> ListModelsAsync(CancellationToken cancellationToken = default)
            => Task.FromException<IReadOnlyList<ModelConfig>>(new HttpRequestException("远端不可达"));
    }
}
