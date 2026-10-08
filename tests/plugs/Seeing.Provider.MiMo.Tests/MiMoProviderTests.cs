using Seeing.Agent.Abstractions.Configuration;
using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Llm;
using Seeing.ConfigSchema;
using Seeing.Provider.MiMo;
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
            var oldClient = new Mock<ILlmClient>();
            var oldDisposable = oldClient.As<IDisposable>();
            var newClient = new Mock<ILlmClient>();
            var factory = new Mock<ILlmClientFactory>();
            factory.Setup(f => f.Create(It.IsAny<ProviderConfig>()))
                .Returns((ProviderConfig cfg) =>
                {
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
            factory.Setup(f => f.Create(It.IsAny<ProviderConfig>()))
                .Returns((ProviderConfig config) =>
                {
                    createdConfig = config;
                    return Mock.Of<ILlmClient>();
                });
            var sut = CreateSut(store, OpenAiFactory(factory));

            await sut.WarmupAsync(TestContext.Current.CancellationToken);
            _ = sut.GetClient();

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
            TestContext.Current.CancellationToken);

        result.Should().BeFalse();
        factory.Verify(f => f.Create(It.IsAny<ProviderConfig>()), Times.Never);
    }

    [Fact]
    public async Task TestConnectionAsync_WithApiKey_DelegatesToClient()
    {
        var client = new Mock<ILlmClient>();
        client.Setup(c => c.TestConnectionAsync(
                "mimo-v2.6-pro",
                It.IsAny<LlmCallContext?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var factory = new Mock<ILlmClientFactory>();
        factory.Setup(f => f.Create(It.IsAny<ProviderConfig>()))
            .Returns(client.Object);
        var sut = CreateSut(apiKey: "sk-test", factory: OpenAiFactory(factory));
        await sut.WarmupAsync(TestContext.Current.CancellationToken);

        var result = await sut.TestConnectionAsync(
            "mimo-v2.6-pro",
            TestContext.Current.CancellationToken);

        result.Should().BeTrue();
        client.Verify(c => c.TestConnectionAsync(
            "mimo-v2.6-pro",
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
}
