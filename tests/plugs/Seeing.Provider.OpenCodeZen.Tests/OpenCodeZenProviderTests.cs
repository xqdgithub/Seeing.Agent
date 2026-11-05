using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Llm;
using Seeing.ConfigSchema;
using System.Net;
using System.Text;
using Xunit;

namespace Seeing.Provider.OpenCodeZen.Tests;

public class OpenCodeZenProviderTests
{
    [Fact]
    public void GetConfigSchema_ExposesOptionalApiKey()
    {
        var sut = CreateSut();
        var schema = sut.GetConfigSchema();
        schema.Should().NotBeNull();
        schema!.Should().ContainSingle(f =>
            f.Name == "ApiKey" && f.Type == ConfigFieldType.Secret && !f.Required);
    }

    [Fact]
    public void Protocol_ShouldBeOpenAi()
    {
        var sut = CreateSut();
        sut.Protocol.Should().Be(ProviderTypes.OpenAi);
    }

    [Fact]
    public void Capabilities_ShouldDeclareChatOnly()
    {
        // OpenCode Zen 走 openai 协议的 chat 端口；embedding 等未经工厂实现，不得虚报能力
        var sut = CreateSut();
        sut.Capabilities.Should().Be(LlmCapabilityKind.Chat);
    }

    [Fact]
    public void GetClient_Chat_ShouldReturnClient()
    {
        var client = new Mock<IChatCompletionClient>();
        var sut = CreateSut(factory: Resolver(OpenAiFactory(client.Object)));

        sut.GetClient(LlmCapabilityKind.Chat).Should().BeSameAs(client.Object);
        // 同一能力重复取用同一实例（缓存键是 providerId + capability，不是 modelId）
        sut.GetClient(LlmCapabilityKind.Chat).Should().BeSameAs(client.Object);
    }

    [Fact]
    public void GetClient_NonChatCapability_ShouldReturnNullWithoutTouchingFactory()
    {
        var factory = new Mock<ILlmClientFactory>();
        var sut = CreateSut(factory: Resolver(OpenAiFactory(factory)));

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
    /// plug Provider 必须原样交出工厂产物：不自建裸客户端、不解包、不二次包装。
    /// <para>plug provider 直接调工厂、从不经过 <c>ConfiguredLlmProvider</c>，所以只要它
    /// 自己动手建客户端或绕过工厂，重试（429 退避）就会静默消失。</para>
    /// <para><b>本用例的边界（诚实标注）</b>：桩工厂自己返回 decorated，因此它只能证明
    /// 「Provider 侧不改写工厂产物」这一半。另一半——「真实工厂确实在 <c>Create</c> 内调
    /// <c>Apply</c>」——由 <c>OpenAiLlmModuleTests.Create_ShouldApplyDecorators_BeforeReturning_AndOnlyHere</c>
    /// 用真实工厂 + 记录型注册表覆盖。两处合起来才是完整的防上移守卫。</para>
    /// </summary>
    [Fact]
    public async Task CreatedClient_ShouldStillBeWrappedByRetryDecorator()
    {
        var inner = new CountingChatClient();
        var decorated = new RetryingDecoratorStub(inner);
        var requestedCapabilities = new List<LlmCapabilityKind>();
        var factory = new Mock<ILlmClientFactory>();
        factory.Setup(f => f.Create(It.IsAny<ProviderConfig>(), It.IsAny<LlmCapabilityKind>()))
            .Returns((ProviderConfig _, LlmCapabilityKind capability) =>
            {
                requestedCapabilities.Add(capability);
                // 真实工厂在此处 `_decorators?.Apply(client, config)`
                return capability == LlmCapabilityKind.Chat ? decorated : null;
            });

        var sut = CreateSut(factory: Resolver(factory.Object));

        var client = sut.GetClient(LlmCapabilityKind.Chat);

        requestedCapabilities.Should().Equal(LlmCapabilityKind.Chat);
        client.Should().BeSameAs(decorated, "Provider 必须交出工厂的装饰产物，而非裸客户端");
        var chatClient = client.Should().BeAssignableTo<IChatCompletionClient>().Subject;

        // 装饰链完整可达：调用先穿过装饰器，再落到内层客户端
        await chatClient.CompleteAsync(
            new ChatRequest { Model = "nemotron-3-ultra-free" },
            call: null,
            TestContext.Current.CancellationToken);

        decorated.CompleteCalls.Should().Be(1);
        inner.CompleteCalls.Should().Be(1);
    }

    [Fact]
    public async Task GetModelsAsync_WithoutApiKey_ReturnsOnlyFreeModels()
    {
        // 未配置 API Key：仅展示免费模型，付费模型被过滤
        var requestCount = 0;
        var handler = new StubHandler(_ =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"data":[{"id":"nemotron-3-ultra-free"},{"id":"deepseek-v4-pro"}]}""",
                    Encoding.UTF8,
                    "application/json")
            };
        });
        var modelsClient = new OpenCodeZenModelsClient(
            handler,
            NullLogger<OpenCodeZenModelsClient>.Instance);
        var sut = CreateSut(modelsClient: modelsClient);
        await sut.WarmupAsync(TestContext.Current.CancellationToken);

        var models = await sut.GetModelsAsync(TestContext.Current.CancellationToken);

        models.Should().ContainSingle();
        models.Single().Id.Should().Be("nemotron-3-ultra-free");
        models.Single().Metadata.Should().ContainKey("isFree");
        models.Single().Pricing!.Input.Should().Be(0);
        requestCount.Should().Be(1);
    }

    [Fact]
    public async Task GetModelsAsync_WithApiKey_ReturnsAllModels()
    {
        var dir = CreateTempDirectory();
        try
        {
            var store = new OpenCodeZenConfigStore(dir, NullLogger<OpenCodeZenConfigStore>.Instance);
            await store.SaveAsync(
                new OpenCodeZenOptions { ApiKey = "sk-zen" },
                TestContext.Current.CancellationToken);
            var modelsClient = new OpenCodeZenModelsClient(
                new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"data":[{"id":"nemotron-3-ultra-free"},{"id":"deepseek-v4-pro"}]}""",
                        Encoding.UTF8,
                        "application/json")
                }),
                NullLogger<OpenCodeZenModelsClient>.Instance);
            var sut = CreateSut(store, modelsClient: modelsClient);
            await sut.WarmupAsync(TestContext.Current.CancellationToken);

            var models = await sut.GetModelsAsync(TestContext.Current.CancellationToken);

            models.Should().HaveCount(2);
            models.Single(m => m.Id == "deepseek-v4-pro").Metadata.Should().BeNull();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task GetModelsAsync_AppliesUserCapabilityOverrides()
    {
        var dir = CreateTempDirectory();
        try
        {
            var store = new OpenCodeZenConfigStore(dir, NullLogger<OpenCodeZenConfigStore>.Instance);
            await store.SaveAsync(
                new OpenCodeZenOptions
                {
                    ApiKey = "sk-zen",
                    ModelCapabilities = new Dictionary<string, ModelCapabilityOverride>
                    {
                        ["deepseek-v4-pro"] = new() { Context = 500_000, Output = 90_000 }
                    }
                },
                TestContext.Current.CancellationToken);
            var modelsClient = new OpenCodeZenModelsClient(
                new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"data":[{"id":"deepseek-v4-pro"}]}""",
                        Encoding.UTF8,
                        "application/json")
                }),
                NullLogger<OpenCodeZenModelsClient>.Instance);
            var sut = CreateSut(store, modelsClient: modelsClient);
            await sut.WarmupAsync(TestContext.Current.CancellationToken);

            var models = await sut.GetModelsAsync(TestContext.Current.CancellationToken);

            var config = models.Single();
            config.Limit.Context.Should().Be(500_000);
            config.Limit.Output.Should().Be(90_000);
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
            var requestCount = 0;
            var handler = new StubHandler(_ =>
            {
                requestCount++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"data":[{"id":"mimo-v2.5-free"}]}""",
                        Encoding.UTF8,
                        "application/json")
                };
            });
            var modelsClient = new OpenCodeZenModelsClient(
                handler,
                NullLogger<OpenCodeZenModelsClient>.Instance);
            var sut = CreateSut(modelsClient: modelsClient);
            await sut.WarmupAsync(TestContext.Current.CancellationToken);

            var first = await sut.GetModelsAsync(TestContext.Current.CancellationToken);
            var second = await sut.GetModelsAsync(TestContext.Current.CancellationToken);

            first.Should().ContainSingle(m => m.Id == "mimo-v2.5-free");
            second.Should().BeSameAs(first);
            requestCount.Should().Be(1);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task CreateClient_WithoutApiKey_SendsOpenCodeFreeTierHeaders()
    {
        ProviderConfig? createdConfig = null;
        var factory = new Mock<ILlmClientFactory>();
        factory.Setup(f => f.Create(It.IsAny<ProviderConfig>(), It.IsAny<LlmCapabilityKind>()))
            .Returns((ProviderConfig config, LlmCapabilityKind _) =>
            {
                createdConfig = config;
                return Mock.Of<IChatCompletionClient>();
            });
        var sut = CreateSut(factory: Resolver(OpenAiFactory(factory)));
        await sut.WarmupAsync(TestContext.Current.CancellationToken);

        _ = sut.GetClient(LlmCapabilityKind.Chat);

        createdConfig.Should().NotBeNull();
        createdConfig!.ApiKey.Should().BeNull();
        // 免认证：不发送 Authorization；静态 UA/client；session 由拦截器按调用注入
        createdConfig.Headers.Should().NotContainKey("Authorization");
        createdConfig.Headers.Should().ContainKey("User-Agent");
        createdConfig.Headers!["User-Agent"].Should().StartWith("opencode/");
        createdConfig.Headers.Should().Contain("x-opencode-client", "cli");
        createdConfig.Headers.Should().NotContainKey("x-opencode-session");
        createdConfig.BaseUrl.Should().Be(OpenCodeZenModelsClient.DefaultBaseUrl);
        createdConfig.Type.Should().Be(ProviderTypes.OpenAi);
    }

    /// <summary>
    /// 协议→工厂是唯一映射：Provider 按自身协议向注册表要工厂，不会误用别的协议的工厂。
    /// <para>从前这里靠「遍历集合、首个 SupportsProtocol 命中」实现，
    /// 现已改为注册表精确查表——本用例锁定「按协议取，不扫集合」。</para>
    /// </summary>
    [Fact]
    public void GetClient_RequestsFactoryByOwnProtocol()
    {
        var openai = new Mock<ILlmClientFactory>();
        var client = Mock.Of<IChatCompletionClient>();
        openai.Setup(f => f.Create(It.IsAny<ProviderConfig>(), It.IsAny<LlmCapabilityKind>())).Returns(client);

        var anthropic = new Mock<ILlmClientFactory>();
        var resolver = new LlmClientFactoryResolver(
        [
            new LlmClientFactoryRegistration(ProviderTypes.Anthropic, anthropic.Object),
            new LlmClientFactoryRegistration(ProviderTypes.OpenAi, openai.Object),
        ]);

        var sut = new OpenCodeZenProvider(
            new OpenCodeZenConfigStore(CreateTempDirectory(), NullLogger<OpenCodeZenConfigStore>.Instance),
            resolver,
            Mock.Of<IProviderRegistry>(),
            new OpenCodeZenModelsClient(NullLogger<OpenCodeZenModelsClient>.Instance),
            PassthroughCapabilityManager(),
            NullLogger<OpenCodeZenProvider>.Instance);

        sut.GetClient(LlmCapabilityKind.Chat).Should().BeSameAs(client);
        anthropic.Verify(f => f.Create(It.IsAny<ProviderConfig>(), It.IsAny<LlmCapabilityKind>()), Times.Never);
        openai.Verify(
            f => f.Create(It.Is<ProviderConfig>(c => c.Type == ProviderTypes.OpenAi), LlmCapabilityKind.Chat),
            Times.Once);
    }

    [Fact]
    public async Task CreateClient_WithApiKey_UsesApiKey()
    {
        var dir = CreateTempDirectory();
        try
        {
            var store = new OpenCodeZenConfigStore(dir, NullLogger<OpenCodeZenConfigStore>.Instance);
            await store.SaveAsync(
                new OpenCodeZenOptions { ApiKey = "sk-zen" },
                TestContext.Current.CancellationToken);
            ProviderConfig? createdConfig = null;
            var factory = new Mock<ILlmClientFactory>();
            factory.Setup(f => f.Create(It.IsAny<ProviderConfig>(), It.IsAny<LlmCapabilityKind>()))
                .Returns((ProviderConfig config, LlmCapabilityKind _) =>
                {
                    createdConfig = config;
                    return Mock.Of<IChatCompletionClient>();
                });
            var sut = CreateSut(store, Resolver(OpenAiFactory(factory)));
            await sut.WarmupAsync(TestContext.Current.CancellationToken);

            _ = sut.GetClient(LlmCapabilityKind.Chat);

            createdConfig!.ApiKey.Should().Be("sk-zen");
            createdConfig.Headers!["User-Agent"].Should().StartWith("opencode/");
            createdConfig.Headers.Should().Contain("x-opencode-client", "cli");
            createdConfig.Headers.Should().NotContainKey("x-opencode-session");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task SaveConfigAsync_PersistsAndRebuildsClient()
    {
        var dir = Path.Combine(Path.GetTempPath(), "seeing-opencodezen-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new OpenCodeZenConfigStore(dir, NullLogger<OpenCodeZenConfigStore>.Instance);
            await store.SaveAsync(
                new OpenCodeZenOptions { ApiKey = "sk-old" },
                TestContext.Current.CancellationToken);

            var oldClient = new Mock<IChatCompletionClient>();
            var oldDisposable = oldClient.As<IDisposable>();
            var newClient = new Mock<IChatCompletionClient>();
            var createdKeys = new List<string?>();
            var factory = new Mock<ILlmClientFactory>();
            factory.Setup(f => f.Create(It.IsAny<ProviderConfig>(), It.IsAny<LlmCapabilityKind>()))
                .Returns((ProviderConfig cfg, LlmCapabilityKind capability) =>
                {
                    capability.Should().Be(LlmCapabilityKind.Chat);
                    createdKeys.Add(cfg.ApiKey);
                    return cfg.ApiKey == "sk-old" ? oldClient.Object : newClient.Object;
                });
            var registry = new Mock<IProviderRegistry>();
            var sut = new OpenCodeZenProvider(
                store,
                Resolver(OpenAiFactory(factory)),
                registry.Object,
                new OpenCodeZenModelsClient(NullLogger<OpenCodeZenModelsClient>.Instance),
                PassthroughCapabilityManager(),
                NullLogger<OpenCodeZenProvider>.Instance);

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
            registry.Verify(r => r.Register(It.Is<ProviderRegistration>(x => ReferenceEquals(x.Provider, sut) && x.OwnerId == OpenCodeZenProvider.ExtensionId)), Times.AtLeastOnce);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task SaveConfigAsync_PreservesUserCapabilityOverrides()
    {
        // WebUI 保存 ApiKey 时不得静默清空用户手写的 modelCapabilities
        var dir = CreateTempDirectory();
        try
        {
            var store = new OpenCodeZenConfigStore(dir, NullLogger<OpenCodeZenConfigStore>.Instance);
            await store.SaveAsync(
                new OpenCodeZenOptions
                {
                    ModelCapabilities = new Dictionary<string, ModelCapabilityOverride>
                    {
                        ["future-free"] = new() { Context = 300_000, Output = 20_000, IsFree = true }
                    }
                },
                TestContext.Current.CancellationToken);
            var sut = CreateSut(store);
            await sut.WarmupAsync(TestContext.Current.CancellationToken);

            await sut.SaveConfigAsync(
                new Dictionary<string, object?> { ["ApiKey"] = "sk-new" },
                ConfigLevel.User,
                TestContext.Current.CancellationToken);

            var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);
            loaded.ApiKey.Should().Be("sk-new");
            loaded.ModelCapabilities.Should().NotBeNull();
            loaded.ModelCapabilities!.Should().ContainKey("future-free");
            loaded.ModelCapabilities["future-free"].Context.Should().Be(300_000);
            loaded.ModelCapabilities["future-free"].IsFree.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task TestConnectionAsync_WithoutApiKey_DelegatesToClient()
    {
        // OpenCode Zen 免费模型无需 API Key 即可测试连接
        var client = new Mock<IChatCompletionClient>();
        client.Setup(c => c.TestConnectionAsync(
                "nemotron-3-ultra-free",
                LlmCapabilityKind.Chat,
                It.IsAny<LlmCallContext?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var factory = new Mock<ILlmClientFactory>();
        factory.Setup(f => f.Create(It.IsAny<ProviderConfig>(), It.IsAny<LlmCapabilityKind>()))
            .Returns(client.Object);
        var sut = CreateSut(factory: Resolver(OpenAiFactory(factory)));
        await sut.WarmupAsync(TestContext.Current.CancellationToken);

        var result = await sut.TestConnectionAsync(
            "nemotron-3-ultra-free",
            LlmCapabilityKind.Chat,
            TestContext.Current.CancellationToken);

        result.Should().BeTrue();
        client.Verify(c => c.TestConnectionAsync(
            "nemotron-3-ultra-free",
            LlmCapabilityKind.Chat,
            It.IsAny<LlmCallContext?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TestConnectionAsync_UndeclaredCapability_ShouldReturnFalseWithoutClient()
    {
        var factory = new Mock<ILlmClientFactory>();
        var sut = CreateSut(factory: Resolver(OpenAiFactory(factory)));
        await sut.WarmupAsync(TestContext.Current.CancellationToken);

        var result = await sut.TestConnectionAsync(
            "nemotron-3-ultra-free",
            LlmCapabilityKind.Embedding,
            TestContext.Current.CancellationToken);

        // Provider 不推导、不猜能力（spec §7.3）：不供出即 false，且不得为它建客户端
        result.Should().BeFalse();
        factory.Verify(
            f => f.Create(It.IsAny<ProviderConfig>(), It.IsAny<LlmCapabilityKind>()),
            Times.Never);
    }

    private static ILlmClientFactory OpenAiFactory(Mock<ILlmClientFactory>? mock = null)
        => (mock ?? new Mock<ILlmClientFactory>()).Object;

    private static ILlmClientFactory OpenAiFactory(IChatCompletionClient client)
    {
        var mock = new Mock<ILlmClientFactory>();
        mock.Setup(f => f.Create(It.IsAny<ProviderConfig>(), LlmCapabilityKind.Chat))
            .Returns(client);
        return mock.Object;
    }

    /// <summary>把单个工厂包成「openai 协议」的解析器（协议→工厂唯一映射）。</summary>
    private static ILlmClientFactoryResolver Resolver(ILlmClientFactory factory)
        => new LlmClientFactoryResolver([new LlmClientFactoryRegistration(ProviderTypes.OpenAi, factory)]);

    private static OpenCodeZenProvider CreateSut(
        OpenCodeZenConfigStore? store = null,
        ILlmClientFactoryResolver? factory = null,
        OpenCodeZenModelsClient? modelsClient = null)
        => new(
            store ?? new OpenCodeZenConfigStore(
                CreateTempDirectory(),
                NullLogger<OpenCodeZenConfigStore>.Instance),
            factory ?? Resolver(OpenAiFactory()),
            Mock.Of<IProviderRegistry>(),
            modelsClient ?? new OpenCodeZenModelsClient(NullLogger<OpenCodeZenModelsClient>.Instance),
            PassthroughCapabilityManager(),
            NullLogger<OpenCodeZenProvider>.Instance);

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
            "seeing-opencodezen-tests",
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

        public string ProviderId => "opencode-zen";

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
