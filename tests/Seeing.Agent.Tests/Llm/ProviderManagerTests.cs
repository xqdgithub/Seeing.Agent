using Seeing.Agent.Abstractions.Configuration;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Configuration;
using Seeing.Agent.Llm;
using Seeing.Agent.Abstractions.Llm;
using Xunit;

namespace Seeing.Agent.Tests.Llm;

public class ProviderManagerTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _tempDirectory =
        Path.Combine(Path.GetTempPath(), "provider-manager-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ProviderReloadHandler_配置变更触发刷新()
    {
        var providers = new Dictionary<string, ProviderConfig>
        {
            ["provider"] = PredefinedProviders.OpenAI("sk-original")
        };
        var configManager = await CreateConfigManagerAsync(new SeeingAgentOptions(), providers);
        var firstClient = Mock.Of<ILlmClient>();
        var secondClient = Mock.Of<ILlmClient>();
        var factory = new Mock<ILlmClientFactory>();
        factory.Setup(candidate => candidate.SupportsType(ProviderType.OpenAI)).Returns(true);
        factory.SetupSequence(candidate => candidate.Create(It.IsAny<ProviderConfig>()))
            .Returns(firstClient)
            .Returns(secondClient);
        using var sut = new ProviderManager(
            configManager,
            factory.Object,
            Mock.Of<IModelConfigManager>(),
            new ProviderRegistry(NullLogger<ProviderRegistry>.Instance),
            NullLogger<ProviderManager>.Instance);
        var handler = new ProviderReloadHandler(sut);

        _ = sut.GetClient("provider");
        await configManager.SaveSectionAsync(
            "Providers",
            new Dictionary<string, ProviderConfig>
            {
                ["provider"] = PredefinedProviders.OpenAI("sk-changed")
            },
            ConfigLevel.User,
            TestContext.Current.CancellationToken);
        await handler.ReloadAsync(
            new ConfigChange { ChangedSections = new[] { "Providers" } },
            TestContext.Current.CancellationToken);

        sut.GetClient("provider").Should().BeSameAs(secondClient);
        factory.Verify(candidate => candidate.Create(It.IsAny<ProviderConfig>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ConfigChanged_ConfiguredProviderOverwrittenByExtension_KeepsExtensionProvider()
    {
        var providers = new Dictionary<string, ProviderConfig>
        {
            ["provider"] = PredefinedProviders.OpenAI("sk-original")
        };
        var configManager = await CreateConfigManagerAsync(new SeeingAgentOptions(), providers);
        var registry = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance);
        var factory = new Mock<ILlmClientFactory>();
        factory.Setup(candidate => candidate.SupportsType(ProviderType.OpenAI)).Returns(true);
        using var sut = new ProviderManager(
            configManager,
            factory.Object,
            Mock.Of<IModelConfigManager>(),
            registry,
            NullLogger<ProviderManager>.Instance);
        var extensionProvider = new TestProvider("provider");
        registry.Register(extensionProvider, ownerExtensionId: "test-extension");
        var changedProviders = new Dictionary<string, ProviderConfig>
        {
            ["provider"] = PredefinedProviders.OpenAI("sk-changed")
        };

        await configManager.SaveSectionAsync(
            "Providers",
            changedProviders,
            ConfigLevel.User,
            TestContext.Current.CancellationToken);

        registry.GetProvider("provider").Should().BeSameAs(extensionProvider);
        sut.GetProvider("provider").Should().BeEquivalentTo(new ProviderInfo
        {
            Id = "provider",
            Name = "provider",
            Source = ProviderSource.Extension,
            OwnerExtensionId = "test-extension",
            MaxRetries = 3
        });
    }

    [Fact]
    public async Task ExtensionProviderUnregistered_ConfiguredProviderIsRestored()
    {
        var providers = new Dictionary<string, ProviderConfig>
        {
            ["provider"] = PredefinedProviders.OpenAI("sk-original")
        };
        var configManager = await CreateConfigManagerAsync(new SeeingAgentOptions(), providers);
        var registry = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance);
        var factory = new Mock<ILlmClientFactory>();
        factory.Setup(candidate => candidate.SupportsType(ProviderType.OpenAI)).Returns(true);
        using var sut = new ProviderManager(
            configManager,
            factory.Object,
            Mock.Of<IModelConfigManager>(),
            registry,
            NullLogger<ProviderManager>.Instance);
        var extensionProvider = new TestProvider("provider");
        registry.Register(extensionProvider, ownerExtensionId: "test-extension");

        registry.UnregisterByOwner("test-extension");

        registry.GetProvider("provider").Should().BeOfType<ConfiguredLlmProvider>();
        sut.GetProvider("provider")!.Source.Should().Be(ProviderSource.Configured);
    }

    [Fact]
    public async Task TestConnectionAsync_DelegatesToRegisteredProvider()
    {
        var configManager = await CreateConfigManagerAsync(new SeeingAgentOptions());
        var registry = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance);
        var provider = new Mock<ILlmProvider>();
        provider.SetupGet(candidate => candidate.Id).Returns("provider");
        provider.Setup(candidate => candidate.TestConnectionAsync(
                "model",
                TestContext.Current.CancellationToken))
            .ReturnsAsync(true);
        provider.Setup(candidate => candidate.GetClient())
            .Returns(Mock.Of<ILlmClient>());
        registry.Register(provider.Object, ownerExtensionId: "test-extension");
        using var sut = new ProviderManager(
            configManager,
            Mock.Of<ILlmClientFactory>(),
            Mock.Of<IModelConfigManager>(),
            registry,
            NullLogger<ProviderManager>.Instance);

        var connected = await sut.TestConnectionAsync(
            "provider",
            "model",
            TestContext.Current.CancellationToken);

        connected.Should().BeTrue();
        provider.Verify(candidate => candidate.TestConnectionAsync(
            "model",
            TestContext.Current.CancellationToken), Times.Once);
    }

    [Fact]
    public async Task Constructor_RegistersConfiguredProvidersAndReturnsProviderInfo()
    {
        var providers = new Dictionary<string, ProviderConfig>
        {
            ["openai"] = PredefinedProviders.OpenAI("sk-test")
        };
        var configManager = await CreateConfigManagerAsync(new SeeingAgentOptions(), providers);
        var registry = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance);
        var factory = new Mock<ILlmClientFactory>();
        factory.Setup(candidate => candidate.SupportsType(ProviderType.OpenAI)).Returns(true);
        factory.Setup(candidate => candidate.Create(It.IsAny<ProviderConfig>()))
            .Returns(Mock.Of<ILlmClient>());
        using var sut = new ProviderManager(
            configManager,
            factory.Object,
            Mock.Of<IModelConfigManager>(),
            registry,
            NullLogger<ProviderManager>.Instance);

        var provider = sut.GetProvider("openai");

        registry.GetProvider("openai").Should().NotBeNull();
        provider.Should().BeEquivalentTo(new ProviderInfo
        {
            Id = "openai",
            Name = "OpenAI",
            Source = ProviderSource.Configured,
            MaxRetries = 3
        });
    }

    [Fact]
    public async Task TryGetConfigurable_ConfiguredProvider_ReturnsTrue()
    {
        var providers = new Dictionary<string, ProviderConfig>
        {
            ["openai"] = PredefinedProviders.OpenAI("sk-test")
        };
        var configManager = await CreateConfigManagerAsync(new SeeingAgentOptions(), providers);
        var registry = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance);
        var factory = new Mock<ILlmClientFactory>();
        factory.Setup(candidate => candidate.SupportsType(ProviderType.OpenAI)).Returns(true);
        factory.Setup(candidate => candidate.Create(It.IsAny<ProviderConfig>()))
            .Returns(Mock.Of<ILlmClient>());
        using var sut = new ProviderManager(
            configManager,
            factory.Object,
            Mock.Of<IModelConfigManager>(),
            registry,
            NullLogger<ProviderManager>.Instance);

        var ok = sut.TryGetConfigurable("openai", out var configurable);

        ok.Should().BeTrue();
        configurable.Should().NotBeNull();
        configurable!.GetConfigSchema().Should().BeNull();
    }

    [Fact]
    public async Task TryGetConfigurable_UnknownId_ReturnsFalse()
    {
        var configManager = await CreateConfigManagerAsync(new SeeingAgentOptions());
        var registry = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance);
        using var sut = new ProviderManager(
            configManager,
            Mock.Of<ILlmClientFactory>(),
            Mock.Of<IModelConfigManager>(),
            registry,
            NullLogger<ProviderManager>.Instance);

        var ok = sut.TryGetConfigurable("missing", out var configurable);

        ok.Should().BeFalse();
        configurable.Should().BeNull();
    }

    [Fact]
    public async Task TryGetConfigurable_ExtensionWithoutInterface_ReturnsFalse()
    {
        var configManager = await CreateConfigManagerAsync(new SeeingAgentOptions());
        var registry = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance);
        registry.Register(new TestProvider("ext"), ownerExtensionId: "test-extension");
        using var sut = new ProviderManager(
            configManager,
            Mock.Of<ILlmClientFactory>(),
            Mock.Of<IModelConfigManager>(),
            registry,
            NullLogger<ProviderManager>.Instance);

        var ok = sut.TryGetConfigurable("ext", out var configurable);

        ok.Should().BeFalse();
        configurable.Should().BeNull();
    }

    [Fact]
    public async Task SaveProviderAsync_ExtensionProvider_DoesNotPersistConfiguration()
    {
        var configManager = await CreateConfigManagerAsync(new SeeingAgentOptions());
        var registry = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance);
        registry.Register(new TestProvider("extension"), ownerExtensionId: "test-extension");
        var factory = new Mock<ILlmClientFactory>();
        var logger = new ListLogger<ProviderManager>();
        using var sut = new ProviderManager(
            configManager,
            factory.Object,
            Mock.Of<IModelConfigManager>(),
            registry,
            logger);

        await sut.SaveProviderAsync(
            "extension",
            PredefinedProviders.OpenAI("sk-test"),
            ct: TestContext.Current.CancellationToken);

        configManager.GetSection<Dictionary<string, ProviderConfig>>("Providers").Should().NotContainKey("extension");
        logger.Levels.Should().Contain(LogLevel.Warning);
    }

    [Fact]
    public async Task RegisterConfiguredProvider_UsesConfigClone_LiveMutationDoesNotAffectCreatedClient()
    {
        var live = PredefinedProviders.OpenAI("sk-original");
        var providers = new Dictionary<string, ProviderConfig>
        {
            ["provider"] = live
        };
        var configManager = await CreateConfigManagerAsync(new SeeingAgentOptions(), providers);
        string? capturedApiKey = null;
        var factory = new Mock<ILlmClientFactory>();
        factory.Setup(candidate => candidate.SupportsType(ProviderType.OpenAI)).Returns(true);
        factory.Setup(candidate => candidate.Create(It.IsAny<ProviderConfig>()))
            .Returns((ProviderConfig cfg) =>
            {
                capturedApiKey = cfg.ApiKey;
                return Mock.Of<ILlmClient>();
            });
        using var sut = new ProviderManager(
            configManager,
            factory.Object,
            Mock.Of<IModelConfigManager>(),
            new ProviderRegistry(NullLogger<ProviderRegistry>.Instance),
            NullLogger<ProviderManager>.Instance);

        live.ApiKey = "sk-mutated-before-client";
        _ = sut.GetClient("provider");

        capturedApiKey.Should().Be("sk-original");
    }

    [Fact]
    public async Task Unregister_DisposesConfiguredProviderClient()
    {
        var providers = new Dictionary<string, ProviderConfig>
        {
            ["provider"] = PredefinedProviders.OpenAI("sk")
        };
        var configManager = await CreateConfigManagerAsync(new SeeingAgentOptions(), providers);
        var registry = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance);
        var disposableClient = new DisposableClient();
        var factory = new Mock<ILlmClientFactory>();
        factory.Setup(candidate => candidate.SupportsType(ProviderType.OpenAI)).Returns(true);
        factory.Setup(candidate => candidate.Create(It.IsAny<ProviderConfig>()))
            .Returns(disposableClient);
        using var sut = new ProviderManager(
            configManager,
            factory.Object,
            Mock.Of<IModelConfigManager>(),
            registry,
            NullLogger<ProviderManager>.Instance);

        _ = sut.GetClient("provider");
        registry.Unregister("provider");

        disposableClient.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task SaveProviderAsync_EmptyModels_PreservesExistingModelsAtLevel()
    {
        var providers = new Dictionary<string, ProviderConfig>
        {
            ["siliconflow"] = new ProviderConfig
            {
                Id = "siliconflow",
                Type = ProviderType.OpenAI,
                ApiKey = "sk-old",
                Models = new Dictionary<string, ModelConfig>
                {
                    ["keep-me"] = new() { Id = "keep-me", Provider = "siliconflow" }
                }
            }
        };
        var configManager = await CreateConfigManagerAsync(new SeeingAgentOptions(), providers);
        var factory = new Mock<ILlmClientFactory>();
        factory.Setup(candidate => candidate.SupportsType(ProviderType.OpenAI)).Returns(true);
        factory.Setup(candidate => candidate.Create(It.IsAny<ProviderConfig>()))
            .Returns(Mock.Of<ILlmClient>());
        using var sut = new ProviderManager(
            configManager,
            factory.Object,
            Mock.Of<IModelConfigManager>(),
            new ProviderRegistry(NullLogger<ProviderRegistry>.Instance),
            NullLogger<ProviderManager>.Instance);

        await sut.SaveProviderAsync(
            "siliconflow",
            new ProviderConfig
            {
                Id = "siliconflow",
                Type = ProviderType.OpenAI,
                ApiKey = "sk-new",
                Models = null
            },
            ConfigLevel.User,
            TestContext.Current.CancellationToken);

        var savedProviders = configManager.GetSection<Dictionary<string, ProviderConfig>>("Providers");
        savedProviders["siliconflow"].ApiKey.Should().Be("sk-new");
        savedProviders["siliconflow"].Models.Should().ContainKey("keep-me");
    }

    private async Task<UnifiedConfigManager> CreateConfigManagerAsync(
        SeeingAgentOptions options,
        Dictionary<string, ProviderConfig>? providers = null)
    {
        var userSeeingDirectory = Path.Combine(_tempDirectory, "user", ".seeing");
        var projectSeeingDirectory = Path.Combine(_tempDirectory, "project", ".seeing");
        Directory.CreateDirectory(userSeeingDirectory);
        Directory.CreateDirectory(projectSeeingDirectory);

        var json = JsonSerializer.Serialize(new { SeeingAgent = options }, JsonOptions);
        await File.WriteAllTextAsync(Path.Combine(userSeeingDirectory, "seeing.json"), json);

        if (providers is { Count: > 0 })
        {
            var providersJson = JsonSerializer.Serialize(providers, JsonOptions);
            await File.WriteAllTextAsync(
                Path.Combine(userSeeingDirectory, "providers.json"), providersJson);
        }

        var workspace = new Mock<IWorkspaceProvider>();
        workspace.Setup(candidate => candidate.WorkspaceRoot).Returns(Path.Combine(_tempDirectory, "project"));
        workspace.Setup(candidate => candidate.UserSeeingDirectory).Returns(userSeeingDirectory);
        workspace.Setup(candidate => candidate.ProjectSeeingDirectory).Returns(projectSeeingDirectory);

        var manager = new UnifiedConfigManager(
            workspace.Object,
            NullLogger<UnifiedConfigManager>.Instance);
        await manager.LoadAsync();
        return manager;
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
            Directory.Delete(_tempDirectory, recursive: true);
    }

    private sealed class TestProvider(string id) : LlmProviderBase
    {
        public override string Id { get; } = id;

        public override string? Name => id;

        public override ILlmClient GetClient() => Mock.Of<ILlmClient>();

        public override Task<IReadOnlyList<ModelConfig>> GetModelsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ModelConfig>>([]);
    }

    private sealed class DisposableClient : ILlmClient, IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public string ProviderId => "provider";

        public ProviderType ProviderType => ProviderType.OpenAI;

        public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<StreamUpdate> CompleteStreamAsync(
            ChatRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> TestConnectionAsync(string modelId, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Levels.Add(logLevel);
    }
}
