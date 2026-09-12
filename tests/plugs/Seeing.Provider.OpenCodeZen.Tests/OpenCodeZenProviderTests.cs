using Seeing.Agent.Abstractions.Configuration;
using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Llm;
using Seeing.ConfigSchema;
using Seeing.Provider.OpenCodeZen;
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
        factory.Setup(f => f.Create(It.IsAny<ProviderConfig>()))
            .Returns((ProviderConfig config) =>
            {
                createdConfig = config;
                return Mock.Of<ILlmClient>();
            });
        var sut = CreateSut(factory: OpenAiFactory(factory));
        await sut.WarmupAsync(TestContext.Current.CancellationToken);

        _ = sut.GetClient();

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

    [Fact]
    public async Task GetClient_WhenAnthropicFactoryRegisteredFirst_UsesOpenAiFactory()
    {
        var anthropic = new Mock<ILlmClientFactory>();
        anthropic.Setup(f => f.SupportsType(It.Is<string>(t =>
            string.Equals(t, ProviderTypes.Anthropic, StringComparison.OrdinalIgnoreCase)))).Returns(true);
        anthropic.Setup(f => f.Create(It.IsAny<ProviderConfig>()))
            .Throws(new NotSupportedException("不支持的 Provider 类型: openai"));

        var openai = new Mock<ILlmClientFactory>();
        var client = Mock.Of<ILlmClient>();
        openai.Setup(f => f.SupportsType(It.Is<string>(t =>
            string.Equals(t, ProviderTypes.OpenAi, StringComparison.OrdinalIgnoreCase)))).Returns(true);
        openai.Setup(f => f.Create(It.IsAny<ProviderConfig>())).Returns(client);

        var sut = new OpenCodeZenProvider(
            new OpenCodeZenConfigStore(CreateTempDirectory(), NullLogger<OpenCodeZenConfigStore>.Instance),
            new[] { anthropic.Object, openai.Object },
            Mock.Of<IProviderRegistry>(),
            new OpenCodeZenModelsClient(NullLogger<OpenCodeZenModelsClient>.Instance),
            PassthroughCapabilityManager(),
            NullLogger<OpenCodeZenProvider>.Instance);

        sut.GetClient().Should().BeSameAs(client);
        anthropic.Verify(f => f.Create(It.IsAny<ProviderConfig>()), Times.Never);
        openai.Verify(f => f.Create(It.Is<ProviderConfig>(c => c.Type == ProviderTypes.OpenAi)), Times.Once);
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
            factory.Setup(f => f.Create(It.IsAny<ProviderConfig>()))
                .Returns((ProviderConfig config) =>
                {
                    createdConfig = config;
                    return Mock.Of<ILlmClient>();
                });
            var sut = CreateSut(store, OpenAiFactory(factory));
            await sut.WarmupAsync(TestContext.Current.CancellationToken);

            _ = sut.GetClient();

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

            var oldClient = new Mock<ILlmClient>();
            var oldDisposable = oldClient.As<IDisposable>();
            var newClient = new Mock<ILlmClient>();
            var createdKeys = new List<string?>();
            var factory = new Mock<ILlmClientFactory>();
            factory.Setup(f => f.Create(It.IsAny<ProviderConfig>()))
                .Returns((ProviderConfig cfg) =>
                {
                    createdKeys.Add(cfg.ApiKey);
                    return cfg.ApiKey == "sk-old" ? oldClient.Object : newClient.Object;
                });
            var registry = new Mock<IProviderRegistry>();
            var sut = new OpenCodeZenProvider(
                store,
                new[] { OpenAiFactory(factory) },
                registry.Object,
                new OpenCodeZenModelsClient(NullLogger<OpenCodeZenModelsClient>.Instance),
                PassthroughCapabilityManager(),
                NullLogger<OpenCodeZenProvider>.Instance);

            await sut.WarmupAsync(TestContext.Current.CancellationToken);
            sut.GetClient().Should().BeSameAs(oldClient.Object);

            await sut.SaveConfigAsync(
                new Dictionary<string, object?> { ["ApiKey"] = "sk-new" },
                ConfigLevel.Project,
                TestContext.Current.CancellationToken);

            var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);
            loaded.ApiKey.Should().Be("sk-new");
            oldDisposable.Verify(d => d.Dispose(), Times.Once);
            sut.GetClient().Should().BeSameAs(newClient.Object);
            createdKeys.Should().Equal("sk-old", "sk-new");
            registry.Verify(r => r.Register(sut, OpenCodeZenProvider.ExtensionId), Times.AtLeastOnce);
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
        var client = new Mock<ILlmClient>();
        client.Setup(c => c.TestConnectionAsync(
                "nemotron-3-ultra-free",
                It.IsAny<LlmCallContext?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var factory = new Mock<ILlmClientFactory>();
        factory.Setup(f => f.Create(It.IsAny<ProviderConfig>()))
            .Returns(client.Object);
        var sut = CreateSut(factory: OpenAiFactory(factory));
        await sut.WarmupAsync(TestContext.Current.CancellationToken);

        var result = await sut.TestConnectionAsync(
            "nemotron-3-ultra-free",
            TestContext.Current.CancellationToken);

        result.Should().BeTrue();
        client.Verify(c => c.TestConnectionAsync(
            "nemotron-3-ultra-free",
            It.IsAny<LlmCallContext?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private static ILlmClientFactory OpenAiFactory(Mock<ILlmClientFactory>? mock = null)
    {
        mock ??= new Mock<ILlmClientFactory>();
        mock.Setup(f => f.SupportsType(It.Is<string>(t =>
            string.Equals(t, ProviderTypes.OpenAi, StringComparison.OrdinalIgnoreCase)))).Returns(true);
        return mock.Object;
    }

    private static OpenCodeZenProvider CreateSut(
        OpenCodeZenConfigStore? store = null,
        ILlmClientFactory? factory = null,
        OpenCodeZenModelsClient? modelsClient = null)
        => new(
            store ?? new OpenCodeZenConfigStore(
                CreateTempDirectory(),
                NullLogger<OpenCodeZenConfigStore>.Instance),
            new[] { factory ?? OpenAiFactory() },
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
}
