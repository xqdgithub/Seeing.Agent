using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Llm;
using Seeing.ConfigSchema;
using System.Net;
using System.Text;
using Xunit;

namespace Seeing.Provider.MiMo.Tests;

public class MiMoProviderTests
{
    [Fact]
    public void GetConfigSchema_ExposesApiKeySecret()
    {
        var sut = CreateSut(apiKey: null);
        var schema = sut.GetConfigSchema();
        schema.Should().NotBeNull();
        schema!.Should().ContainSingle(f =>
            f.Name == "ApiKey" && f.Type == ConfigFieldType.Secret && f.Required);
    }

    [Fact]
    public void Protocol_ShouldBeOpenAi()
    {
        var sut = CreateSut(apiKey: null);
        sut.Protocol.Should().Be(ProviderTypes.OpenAi);
    }

    [Fact]
    public void Capabilities_ShouldDeclareChatOnly()
    {
        // MiMo 走 openai 协议的 chat 端口；embedding 等未经工厂实现，不得虚报能力
        var sut = CreateSut(apiKey: null);
        sut.Capabilities.Should().Be(LlmCapabilityKind.Chat);
    }

    [Fact]
    public void GetClient_Chat_ShouldReturnClient()
    {
        var client = new Mock<IChatCompletionClient>();
        var sut = CreateSut(apiKey: "sk-test", factory: OpenAiFactory(client.Object));

        sut.GetClient(LlmCapabilityKind.Chat).Should().BeSameAs(client.Object);
        // 同一能力重复取用同一实例（缓存键是 providerId + capability，不是 modelId）
        sut.GetClient(LlmCapabilityKind.Chat).Should().BeSameAs(client.Object);
    }

    [Fact]
    public void GetClient_NonChatCapability_ShouldReturnNullWithoutTouchingFactory()
    {
        var factory = new Mock<ILlmClientFactory>();
        var sut = CreateSut(apiKey: "sk-test", factory: OpenAiFactory(factory));

        foreach (var capability in new[]
        {
            LlmCapabilityKind.None,
            LlmCapabilityKind.Embedding,
            LlmCapabilityKind.Rerank,
            LlmCapabilityKind.Decision,
            LlmCapabilityKind.SpeechSynthesis,
            LlmCapabilityKind.SpeechTranscription,
            LlmCapabilityKind.ImageGeneration,
            LlmCapabilityKind.VideoGeneration
        })
        {
            sut.GetClient(capability).Should().BeNull(because: $"{capability} 不由本 Provider 供出");
        }

        factory.Verify(f => f.Create(It.IsAny<ProviderConfig>(), It.IsAny<LlmCapabilityKind>()), Times.Never);
    }

    /// <summary>
    /// 防 B2 回归：装饰器（重试 / 429 退避）的唯一应用点在工厂 <c>Create</c> 返回之前（spec §7.3）。
    /// plug provider 直接调工厂、从不经过 <c>ConfiguredLlmProvider</c>，因此一旦把 <c>Apply</c>
    /// 上移到 Provider 层，本 Provider 拿到的就是未装饰的裸客户端——重试会**静默消失**。
    /// 桩工厂复刻真实工厂的顺序（建裸客户端 → 内部 Apply 装饰器 → 交回最外层），
    /// 断言 Provider 原样交出装饰产物：既不自建裸客户端、也不解包或二次包装。
    /// </summary>
    [Fact]
    public async Task CreatedClient_ShouldStillBeWrappedByRetryDecorator()
    {
        var inner = new CountingChatClient();
        var decorated = new RetryingDecoratorStub(inner);
        var requestedCapabilities = new List<LlmCapabilityKind>();
        var factory = new Mock<ILlmClientFactory>();
        factory.Setup(f => f.SupportsProtocol(ProviderTypes.OpenAi)).Returns(true);
        factory.Setup(f => f.Create(It.IsAny<ProviderConfig>(), It.IsAny<LlmCapabilityKind>()))
            .Returns((ProviderConfig _, LlmCapabilityKind capability) =>
            {
                requestedCapabilities.Add(capability);
                // 真实工厂在此处 `_decorators?.Apply(client, config)`
                return capability == LlmCapabilityKind.Chat ? decorated : null;
            });

        var sut = CreateSut(apiKey: "sk-test", factory: factory.Object);

        var client = sut.GetClient(LlmCapabilityKind.Chat);

        requestedCapabilities.Should().Equal(LlmCapabilityKind.Chat);
        client.Should().BeSameAs(decorated, "Provider 必须交出工厂的装饰产物，而非裸客户端");
        var chatClient = client.Should().BeAssignableTo<IChatCompletionClient>().Subject;

        // 装饰链完整可达：调用先穿过装饰器，再落到内层客户端
        await chatClient.CompleteAsync(
            new ChatRequest { Model = "mimo-v2.6-pro" },
            call: null,
            TestContext.Current.CancellationToken);

        decorated.CompleteCalls.Should().Be(1);
        inner.CompleteCalls.Should().Be(1);
    }

    [Fact]
    public async Task GetModelsAsync_WithoutApiKey_ReturnsEmpty()
    {
        var sut = CreateSut(apiKey: null);
        await sut.WarmupAsync(TestContext.Current.CancellationToken);
        var models = await sut.GetModelsAsync(TestContext.Current.CancellationToken);
        models.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveConfigAsync_PersistsAndRebuildsClient()
    {
        var dir = Path.Combine(Path.GetTempPath(), "seeing-mimo-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new MiMoConfigStore(dir, NullLogger<MiMoConfigStore>.Instance);
            await store.SaveAsync(
                new MiMoOptions { ApiKey = "sk-old" },
                TestContext.Current.CancellationToken);

            var createdKeys = new List<string?>();
            var oldClient = new Mock<IChatCompletionClient>();
            var oldDisposable = oldClient.As<IDisposable>();
            var newClient = new Mock<IChatCompletionClient>();
            var factory = new Mock<ILlmClientFactory>();
            factory.Setup(f => f.Create(It.IsAny<ProviderConfig>(), It.IsAny<LlmCapabilityKind>()))
                .Returns((ProviderConfig cfg, LlmCapabilityKind capability) =>
                {
                    capability.Should().Be(LlmCapabilityKind.Chat);
                    createdKeys.Add(cfg.ApiKey);
                    cfg.BaseUrl.Should().Be(MiMoModelsClient.DefaultBaseUrl);
                    cfg.Type.Should().Be(ProviderTypes.OpenAi);
                    cfg.Id.Should().Be("mimo");
                    return cfg.ApiKey == "sk-old" ? oldClient.Object : newClient.Object;
                });
            var registry = new Mock<IProviderRegistry>();
            var sut = new MiMoProvider(
                store,
                new[] { OpenAiFactory(factory) },
                registry.Object,
                new MiMoModelsClient(NullLogger<MiMoModelsClient>.Instance),
                PassthroughCapabilityManager(),
                NullLogger<MiMoProvider>.Instance);

            await sut.WarmupAsync(TestContext.Current.CancellationToken);
            sut.GetClient(LlmCapabilityKind.Chat).Should().BeSameAs(oldClient.Object);

            await sut.SaveConfigAsync(
                new Dictionary<string, object?> { ["ApiKey"] = "sk-new" },
                ConfigLevel.Project,
                TestContext.Current.CancellationToken);

            var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);
            loaded.ApiKey.Should().Be("sk-new");
            oldDisposable.Verify(d => d.Dispose(), Times.Once);
            sut.GetClient(LlmCapabilityKind.Chat).Should().BeSameAs(newClient.Object);
            createdKeys.Should().Equal("sk-old", "sk-new");
            registry.Verify(r => r.Register(sut, MiMoProvider.ExtensionId), Times.AtLeastOnce);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task WarmupAsync_LoadsApiKeyFromStore()
    {
        var dir = CreateTempDirectory();
        try
        {
            var store = new MiMoConfigStore(dir, NullLogger<MiMoConfigStore>.Instance);
            await store.SaveAsync(
                new MiMoOptions { ApiKey = "sk-stored" },
                TestContext.Current.CancellationToken);
            ProviderConfig? createdConfig = null;
            var factory = new Mock<ILlmClientFactory>();
            factory.Setup(f => f.Create(It.IsAny<ProviderConfig>(), It.IsAny<LlmCapabilityKind>()))
                .Returns((ProviderConfig config, LlmCapabilityKind _) =>
                {
                    createdConfig = config;
                    return Mock.Of<IChatCompletionClient>();
                });
            var sut = CreateSut(store, OpenAiFactory(factory));

            await sut.WarmupAsync(TestContext.Current.CancellationToken);
            _ = sut.GetClient(LlmCapabilityKind.Chat);

            createdConfig.Should().NotBeNull();
            createdConfig!.ApiKey.Should().Be("sk-stored");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task GetModelsAsync_WithinTtl_UsesCachedResponse()
    {
        var dir = CreateTempDirectory();
        try
        {
            var store = new MiMoConfigStore(dir, NullLogger<MiMoConfigStore>.Instance);
            await store.SaveAsync(
                new MiMoOptions { ApiKey = "sk-models" },
                TestContext.Current.CancellationToken);
            var requestCount = 0;
            var handler = new StubHandler(_ =>
            {
                requestCount++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"object":"list","data":[{"id":"mimo-v2.6-pro","object":"model","owned_by":"xiaomi"}]}""",
                        Encoding.UTF8,
                        "application/json")
                };
            });
            var modelsClient = new MiMoModelsClient(
                handler,
                NullLogger<MiMoModelsClient>.Instance);
            var sut = CreateSut(store, modelsClient: modelsClient);
            await sut.WarmupAsync(TestContext.Current.CancellationToken);

            var first = await sut.GetModelsAsync(TestContext.Current.CancellationToken);
            var second = await sut.GetModelsAsync(TestContext.Current.CancellationToken);

            first.Should().ContainSingle(m => m.Id == "mimo-v2.6-pro");
            second.Should().BeSameAs(first);
            requestCount.Should().Be(1);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task TestConnectionAsync_WithoutApiKey_ReturnsFalseWithoutClient()
    {
        var factory = new Mock<ILlmClientFactory>();
        var sut = CreateSut(apiKey: null, factory: OpenAiFactory(factory));
        await sut.WarmupAsync(TestContext.Current.CancellationToken);

        var result = await sut.TestConnectionAsync(
            "mimo-v2.6-pro",
            LlmCapabilityKind.Chat,
            TestContext.Current.CancellationToken);

        result.Should().BeFalse();
        factory.Verify(
            f => f.Create(It.IsAny<ProviderConfig>(), It.IsAny<LlmCapabilityKind>()),
            Times.Never);
    }

    [Fact]
    public async Task TestConnectionAsync_WithApiKey_DelegatesToClient()
    {
        var client = new Mock<IChatCompletionClient>();
        client.Setup(c => c.TestConnectionAsync(
                "mimo-v2.6-pro",
                LlmCapabilityKind.Chat,
                It.IsAny<LlmCallContext?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var factory = new Mock<ILlmClientFactory>();
        factory.Setup(f => f.Create(It.IsAny<ProviderConfig>(), It.IsAny<LlmCapabilityKind>()))
            .Returns(client.Object);
        var sut = CreateSut(apiKey: "sk-test", factory: OpenAiFactory(factory));
        await sut.WarmupAsync(TestContext.Current.CancellationToken);

        var result = await sut.TestConnectionAsync(
            "mimo-v2.6-pro",
            LlmCapabilityKind.Chat,
            TestContext.Current.CancellationToken);

        result.Should().BeTrue();
        client.Verify(c => c.TestConnectionAsync(
            "mimo-v2.6-pro",
            LlmCapabilityKind.Chat,
            It.IsAny<LlmCallContext?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TestConnectionAsync_UndeclaredCapability_ShouldReturnFalseWithoutClient()
    {
        var factory = new Mock<ILlmClientFactory>();
        var sut = CreateSut(apiKey: "sk-test", factory: OpenAiFactory(factory));
        await sut.WarmupAsync(TestContext.Current.CancellationToken);

        var result = await sut.TestConnectionAsync(
            "mimo-v2.6-pro",
            LlmCapabilityKind.Embedding,
            TestContext.Current.CancellationToken);

        // Provider 不推导、不猜能力（spec §7.3）：不供出即 false，且不得为它建客户端
        result.Should().BeFalse();
        factory.Verify(
            f => f.Create(It.IsAny<ProviderConfig>(), It.IsAny<LlmCapabilityKind>()),
            Times.Never);
    }

    private static ILlmClientFactory OpenAiFactory(Mock<ILlmClientFactory>? mock = null)
    {
        mock ??= new Mock<ILlmClientFactory>();
        mock.Setup(f => f.SupportsProtocol(It.Is<string>(t =>
            string.Equals(t, ProviderTypes.OpenAi, StringComparison.OrdinalIgnoreCase)))).Returns(true);
        return mock.Object;
    }

    private static ILlmClientFactory OpenAiFactory(IChatCompletionClient client)
    {
        var mock = new Mock<ILlmClientFactory>();
        mock.Setup(f => f.SupportsProtocol(ProviderTypes.OpenAi)).Returns(true);
        mock.Setup(f => f.Create(It.IsAny<ProviderConfig>(), LlmCapabilityKind.Chat))
            .Returns(client);
        return mock.Object;
    }

    private static MiMoProvider CreateSut(
        string? apiKey,
        ILlmClientFactory? factory = null)
    {
        var dir = CreateTempDirectory();
        var store = new MiMoConfigStore(dir, NullLogger<MiMoConfigStore>.Instance);
        if (!string.IsNullOrEmpty(apiKey))
            store.SaveAsync(new MiMoOptions { ApiKey = apiKey }).GetAwaiter().GetResult();

        return CreateSut(store, factory);
    }

    private static MiMoProvider CreateSut(
        MiMoConfigStore store,
        ILlmClientFactory? factory = null,
        MiMoModelsClient? modelsClient = null)
    {
        return new MiMoProvider(
            store,
            new[] { factory ?? OpenAiFactory() },
            Mock.Of<IProviderRegistry>(),
            modelsClient ?? new MiMoModelsClient(NullLogger<MiMoModelsClient>.Instance),
            PassthroughCapabilityManager(),
            NullLogger<MiMoProvider>.Instance);
    }

    private static IModelCapabilityManager PassthroughCapabilityManager()
    {
        var mock = new Mock<IModelCapabilityManager>();
        mock.Setup(m => m.TryEnrichIfEnabledAsync(It.IsAny<ModelConfig>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ModelConfig m, CancellationToken _) => m);
        return mock.Object;
    }

    private static string CreateTempDirectory()
    {
        var dir = Path.Combine(
            Path.GetTempPath(),
            "seeing-mimo-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }

    /// <summary>裸客户端（装饰器内层）：记录补全调用与释放。</summary>
    private sealed class CountingChatClient : IChatCompletionClient, IDisposable
    {
        public int CompleteCalls { get; private set; }

        public bool Disposed { get; private set; }

        public string ProviderId => "mimo";

        public string ProviderType => ProviderTypes.OpenAi;

        public Task<ChatResponse> CompleteAsync(
            ChatRequest request,
            LlmCallContext? call = null,
            CancellationToken ct = default)
        {
            CompleteCalls++;
            return Task.FromResult(new ChatResponse());
        }

        public IAsyncEnumerable<StreamUpdate> CompleteStreamAsync(
            ChatRequest request,
            LlmCallContext? call = null,
            CancellationToken ct = default)
            => throw new NotSupportedException("测试桩不需要流式。");

        public Task<bool> TestConnectionAsync(
            string modelId,
            LlmCapabilityKind capability,
            LlmCallContext? call = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(capability == LlmCapabilityKind.Chat);

        public void Dispose() => Disposed = true;
    }

    /// <summary>装饰器产物桩（等价 Core 的 <c>RetryingLlmClient</c>）：委托内层并记录自身被调用。</summary>
    private sealed class RetryingDecoratorStub : IChatCompletionClient
    {
        public RetryingDecoratorStub(IChatCompletionClient inner) => Inner = inner;

        public IChatCompletionClient Inner { get; }

        public int CompleteCalls { get; private set; }

        public string ProviderId => Inner.ProviderId;

        public string ProviderType => Inner.ProviderType;

        public async Task<ChatResponse> CompleteAsync(
            ChatRequest request,
            LlmCallContext? call = null,
            CancellationToken ct = default)
        {
            CompleteCalls++;
            return await Inner.CompleteAsync(request, call, ct);
        }

        public IAsyncEnumerable<StreamUpdate> CompleteStreamAsync(
            ChatRequest request,
            LlmCallContext? call = null,
            CancellationToken ct = default)
            => Inner.CompleteStreamAsync(request, call, ct);

        public Task<bool> TestConnectionAsync(
            string modelId,
            LlmCapabilityKind capability,
            LlmCallContext? call = null,
            CancellationToken cancellationToken = default)
            => Inner.TestConnectionAsync(modelId, capability, call, cancellationToken);
    }
}
