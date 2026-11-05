using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Llm;
using Seeing.Agent.Llm;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    public void RequiresRebuild_ModelDirectoryChangeOnly_ReturnsFalse()
    {
        var previous = new ProviderConfig
        {
            Id = "p",
            Type = ProviderTypes.OpenAi,
            Models = new Dictionary<string, ModelConfig> { ["a"] = new() { Id = "a" } }
        };
        var current = new ProviderConfig
        {
            Id = "p",
            Type = ProviderTypes.OpenAi,
            Models = new Dictionary<string, ModelConfig> { ["b"] = new() { Id = "b" } }
        };

        ProviderManager.RequiresRebuild(previous, current).Should().BeFalse();
    }

    [Fact]
    public void RequiresRebuild_ConnectionChange_ReturnsTrue()
    {
        var previous = new ProviderConfig
        {
            Id = "p",
            Type = ProviderTypes.OpenAi,
            BaseUrl = "https://old"
        };
        var current = new ProviderConfig
        {
            Id = "p",
            Type = ProviderTypes.OpenAi,
            BaseUrl = "https://new"
        };

        ProviderManager.RequiresRebuild(previous, current).Should().BeTrue();
    }

    [Fact]
    public void RequiresRebuild_CapabilityAdded_ReturnsTrue()
    {
        var previous = new ProviderConfig
        {
            Id = "p",
            Type = ProviderTypes.OpenAi,
            Capabilities = ["chat"]
        };
        var current = new ProviderConfig
        {
            Id = "p",
            Type = ProviderTypes.OpenAi,
            Capabilities = ["chat", "embedding"]
        };

        ProviderManager.RequiresRebuild(previous, current).Should().BeTrue("能力集变化必须重建客户端束");
    }

    [Fact]
    public void RequiresRebuild_CapabilityOrderOrCaseDiffers_ReturnsFalse()
    {
        var previous = new ProviderConfig
        {
            Id = "p",
            Type = ProviderTypes.OpenAi,
            Capabilities = ["chat", "embedding"]
        };
        var current = new ProviderConfig
        {
            Id = "p",
            Type = ProviderTypes.OpenAi,
            Capabilities = ["Embedding", "Chat"]
        };

        ProviderManager.RequiresRebuild(previous, current).Should().BeFalse("能力集等价（大小写/顺序无关）");
    }

    [Fact]
    public void RequiresRebuild_ListModelsChanged_ReturnsTrue()
    {
        var previous = new ProviderConfig { Id = "p", Type = ProviderTypes.OpenAi, ListModels = false };
        var current = new ProviderConfig { Id = "p", Type = ProviderTypes.OpenAi, ListModels = true };

        ProviderManager.RequiresRebuild(previous, current).Should().BeTrue("动态目录开关变化必须重建");
    }

    [Fact]
    public void RequiresRebuild_ShouldNotCompareDefaultModel()
    {
        // 决议 22：ProviderConfig.DefaultModel 已删除；重建判定只看连接与能力字段
        typeof(ProviderConfig).GetProperty("DefaultModel").Should().BeNull();

        var baseline = new ProviderConfig { Id = "p", Type = ProviderTypes.OpenAi };
        ProviderManager.RequiresRebuild(baseline, Clone(baseline)).Should().BeFalse();

        static ProviderConfig Clone(ProviderConfig source) => new()
        {
            Id = source.Id,
            Type = source.Type,
            BaseUrl = source.BaseUrl,
            ApiKey = source.ApiKey
        };
    }

    [Fact]
    public async Task ProviderReloadHandler_配置变更触发刷新()
    {
        var providers = new Dictionary<string, ProviderConfig>
        {
            ["provider"] = PredefinedProviders.OpenAI("sk-original")
        };
        var configManager = await CreateConfigManagerAsync(new SeeingAgentOptions(), providers);
        var firstClient = Mock.Of<IChatCompletionClient>();
        var secondClient = Mock.Of<IChatCompletionClient>();
        var factory = new Mock<ILlmClientFactory>();
        factory.SetupSequence(candidate => candidate.Create(It.IsAny<ProviderConfig>(), It.IsAny<LlmCapabilityKind>()))
            .Returns(firstClient)
            .Returns(secondClient);
        using var sut = new ProviderManager(
            configManager,
            Resolver(factory.Object),
            Mock.Of<IModelConfigManager>(),
            new ProviderRegistry(NullLogger<ProviderRegistry>.Instance),
            new Lazy<IModelCapabilityManager>(() => NullModelCapabilityManager.Instance),
            NullLogger<ProviderManager>.Instance);
        var handler = new ProviderReloadHandler(sut);

        _ = sut.GetClient("provider", LlmCapabilityKind.Chat);
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

        sut.GetClient("provider", LlmCapabilityKind.Chat).Should().BeSameAs(secondClient);
        factory.Verify(candidate => candidate.Create(It.IsAny<ProviderConfig>(), It.IsAny<LlmCapabilityKind>()), Times.Exactly(2));
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
        using var sut = new ProviderManager(
            configManager,
            Resolver(factory.Object),
            Mock.Of<IModelConfigManager>(),
            registry,
            new Lazy<IModelCapabilityManager>(() => NullModelCapabilityManager.Instance),
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
            // LlmProviderBase 基线投影：未声明协议为空串，能力缺省只给 Chat
            Protocol = string.Empty,
            Capabilities = LlmCapabilityKind.Chat,
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
        using var sut = new ProviderManager(
            configManager,
            Resolver(factory.Object),
            Mock.Of<IModelConfigManager>(),
            registry,
            new Lazy<IModelCapabilityManager>(() => NullModelCapabilityManager.Instance),
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
                LlmCapabilityKind.Chat,
                TestContext.Current.CancellationToken))
            .ReturnsAsync(true);
        provider.Setup(candidate => candidate.GetClient(It.IsAny<LlmCapabilityKind>()))
            .Returns(Mock.Of<IChatCompletionClient>());
        registry.Register(provider.Object, ownerExtensionId: "test-extension");
        using var sut = new ProviderManager(
            configManager,
            Resolver(Mock.Of<ILlmClientFactory>()),
            Mock.Of<IModelConfigManager>(),
            registry,
            new Lazy<IModelCapabilityManager>(() => NullModelCapabilityManager.Instance),
            NullLogger<ProviderManager>.Instance);

        var connected = await sut.TestConnectionAsync(
            "provider",
            "model",
            LlmCapabilityKind.Chat,
            TestContext.Current.CancellationToken);

        connected.Should().BeTrue();
        provider.Verify(candidate => candidate.TestConnectionAsync(
            "model",
            LlmCapabilityKind.Chat,
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
        factory.Setup(candidate => candidate.Create(It.IsAny<ProviderConfig>(), It.IsAny<LlmCapabilityKind>()))
            .Returns(Mock.Of<IChatCompletionClient>());
        using var sut = new ProviderManager(
            configManager,
            Resolver(factory.Object),
            Mock.Of<IModelConfigManager>(),
            registry,
            new Lazy<IModelCapabilityManager>(() => NullModelCapabilityManager.Instance),
            NullLogger<ProviderManager>.Instance);

        var provider = sut.GetProvider("openai");

        registry.GetProvider("openai").Should().NotBeNull();
        provider.Should().BeEquivalentTo(new ProviderInfo
        {
            Id = "openai",
            Name = "OpenAI",
            // 配置驱动 Provider：协议取 config.Type，能力缺省只给 Chat（决议 15/§7.1）
            Protocol = ProviderTypes.OpenAi,
            Capabilities = LlmCapabilityKind.Chat,
            Source = ProviderSource.Configured,
            MaxRetries = 0
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
        factory.Setup(candidate => candidate.Create(It.IsAny<ProviderConfig>(), It.IsAny<LlmCapabilityKind>()))
            .Returns(Mock.Of<IChatCompletionClient>());
        using var sut = new ProviderManager(
            configManager,
            Resolver(factory.Object),
            Mock.Of<IModelConfigManager>(),
            registry,
            new Lazy<IModelCapabilityManager>(() => NullModelCapabilityManager.Instance),
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
            Resolver(Mock.Of<ILlmClientFactory>()),
            Mock.Of<IModelConfigManager>(),
            registry,
            new Lazy<IModelCapabilityManager>(() => NullModelCapabilityManager.Instance),
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
            Resolver(Mock.Of<ILlmClientFactory>()),
            Mock.Of<IModelConfigManager>(),
            registry,
            new Lazy<IModelCapabilityManager>(() => NullModelCapabilityManager.Instance),
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
            Resolver(factory.Object),
            Mock.Of<IModelConfigManager>(),
            registry,
            new Lazy<IModelCapabilityManager>(() => NullModelCapabilityManager.Instance),
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
        factory.Setup(candidate => candidate.Create(It.IsAny<ProviderConfig>(), It.IsAny<LlmCapabilityKind>()))
            .Returns((ProviderConfig cfg, LlmCapabilityKind _) =>
            {
                capturedApiKey = cfg.ApiKey;
                return Mock.Of<IChatCompletionClient>();
            });
        using var sut = new ProviderManager(
            configManager,
            Resolver(factory.Object),
            Mock.Of<IModelConfigManager>(),
            new ProviderRegistry(NullLogger<ProviderRegistry>.Instance),
            new Lazy<IModelCapabilityManager>(() => NullModelCapabilityManager.Instance),
            NullLogger<ProviderManager>.Instance);

        live.ApiKey = "sk-mutated-before-client";
        _ = sut.GetClient("provider", LlmCapabilityKind.Chat);

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
        factory.Setup(candidate => candidate.Create(It.IsAny<ProviderConfig>(), It.IsAny<LlmCapabilityKind>()))
            .Returns(disposableClient);
        using var sut = new ProviderManager(
            configManager,
            Resolver(factory.Object),
            Mock.Of<IModelConfigManager>(),
            registry,
            new Lazy<IModelCapabilityManager>(() => NullModelCapabilityManager.Instance),
            NullLogger<ProviderManager>.Instance);

        _ = sut.GetClient("provider", LlmCapabilityKind.Chat);
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
                Type = ProviderTypes.OpenAi,
                ApiKey = "sk-old",
                Models = new Dictionary<string, ModelConfig>
                {
                    ["keep-me"] = new() { Id = "keep-me", Provider = "siliconflow" }
                }
            }
        };
        var configManager = await CreateConfigManagerAsync(new SeeingAgentOptions(), providers);
        var factory = new Mock<ILlmClientFactory>();
        factory.Setup(candidate => candidate.Create(It.IsAny<ProviderConfig>(), It.IsAny<LlmCapabilityKind>()))
            .Returns(Mock.Of<IChatCompletionClient>());
        using var sut = new ProviderManager(
            configManager,
            Resolver(factory.Object),
            Mock.Of<IModelConfigManager>(),
            new ProviderRegistry(NullLogger<ProviderRegistry>.Instance),
            new Lazy<IModelCapabilityManager>(() => NullModelCapabilityManager.Instance),
            NullLogger<ProviderManager>.Instance);

        await sut.SaveProviderAsync(
            "siliconflow",
            new ProviderConfig
            {
                Id = "siliconflow",
                Type = ProviderTypes.OpenAi,
                ApiKey = "sk-new",
                Models = null
            },
            ConfigLevel.User,
            TestContext.Current.CancellationToken);

        var savedProviders = configManager.GetSection<Dictionary<string, ProviderConfig>>("Providers");
        savedProviders["siliconflow"].ApiKey.Should().Be("sk-new");
        savedProviders["siliconflow"].Models.Should().ContainKey("keep-me");
    }

    [Fact]
    public async Task SaveProviderAsync_ConnectionChange_ShouldNotReenterRegistryChangedAndDuplicateRegister()
    {
        // I2 回归：RefreshConfiguredProviders 持 _sync 调 Register/Unregister 会同步 Raise ProvidersChanged；
        // 同线程重入 OnProvidersChanged 曾导致基于中间快照重复构造/释放被替换 Provider。
        var providers = new Dictionary<string, ProviderConfig>
        {
            ["provider"] = PredefinedProviders.OpenAI("sk-original")
        };
        var configManager = await CreateConfigManagerAsync(new SeeingAgentOptions(), providers);
        var registry = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance);
        var factory = new Mock<ILlmClientFactory>();
        factory.Setup(candidate => candidate.Create(It.IsAny<ProviderConfig>(), It.IsAny<LlmCapabilityKind>()))
            .Returns(Mock.Of<IChatCompletionClient>());
        using var sut = new ProviderManager(
            configManager,
            Resolver(factory.Object),
            Mock.Of<IModelConfigManager>(),
            registry,
            new Lazy<IModelCapabilityManager>(() => NullModelCapabilityManager.Instance),
            NullLogger<ProviderManager>.Instance);

        var changes = new List<ProvidersChangedEventArgs>();
        registry.ProvidersChanged += (_, e) => changes.Add(e);

        await sut.SaveProviderAsync(
            "provider",
            PredefinedProviders.OpenAI("sk-changed"),
            ConfigLevel.User,
            TestContext.Current.CancellationToken);

        // 一次重建应仅 Unregister + Register 各一次（重入会额外 Register）
        changes.Should().HaveCount(2);
        changes.Count(e => e.ChangedProviderIds.Contains("provider")).Should().Be(1);
        changes.Count(e => e.RemovedProviderIds.Contains("provider")).Should().Be(1);
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
        workspace.Setup(candidate => candidate.UserSeeingDirectory).Returns(userSeeingDirectory);
        workspace.Setup(candidate => candidate.ProjectSeeingDirectory).Returns(projectSeeingDirectory);

        var manager = new UnifiedConfigManager(workspace.Object, NullLogger<UnifiedConfigManager>.Instance, ConfigSectionRegistry.CreateWithSpine());
        await manager.LoadAsync();
        return manager;
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
            Directory.Delete(_tempDirectory, recursive: true);
    }

    [Fact]
    public async Task ResolveFactory_TwoFactories_SupportsProtocolIsCaseInsensitive()
    {
        var configManager = await CreateConfigManagerAsync(new SeeingAgentOptions());
        var openAi = new Mock<ILlmClientFactory>();
        var anthropic = new Mock<ILlmClientFactory>();

        using var sut = new ProviderManager(
            configManager,
            Resolver((ProviderTypes.OpenAi, openAi.Object), (ProviderTypes.Anthropic, anthropic.Object)),
            Mock.Of<IModelConfigManager>(),
            new ProviderRegistry(NullLogger<ProviderRegistry>.Instance),
            new Lazy<IModelCapabilityManager>(() => NullModelCapabilityManager.Instance),
            NullLogger<ProviderManager>.Instance);

        sut.ResolveFactory("OpenAI").Should().BeSameAs(openAi.Object);
        sut.ResolveFactory("openai").Should().BeSameAs(openAi.Object);
        sut.ResolveFactory("Anthropic").Should().BeSameAs(anthropic.Object);
    }

    /// <summary>
    /// 同一协议登记两个不同工厂 → 组合期即抛。
    /// <para>从前是 first-wins 静默覆盖（后者永远不被用到且无从发现）；现在改为注册表构造期拒绝。</para>
    /// </summary>
    [Fact]
    public async Task ResolveFactory_SameProtocolTwoFactories_ShouldThrow()
    {
        var configManager = await CreateConfigManagerAsync(new SeeingAgentOptions());
        var first = new Mock<ILlmClientFactory>();
        var second = new Mock<ILlmClientFactory>();

        var act = () => new ProviderManager(
            configManager,
            Resolver((ProviderTypes.OpenAi, first.Object), (ProviderTypes.OpenAi, second.Object)),
            Mock.Of<IModelConfigManager>(),
            new ProviderRegistry(NullLogger<ProviderRegistry>.Instance),
            new Lazy<IModelCapabilityManager>(() => NullModelCapabilityManager.Instance),
            NullLogger<ProviderManager>.Instance);

        act.Should().Throw<InvalidOperationException>().WithMessage("*重复登记*");
    }

    /// <summary>同一工厂登记多个协议是允许的（OpenAI 工厂同时服务两个协议）。</summary>
    [Fact]
    public async Task ResolveFactory_SameFactoryTwoProtocols_IsAllowed()
    {
        var configManager = await CreateConfigManagerAsync(new SeeingAgentOptions());
        var factory = new Mock<ILlmClientFactory>();

        using var sut = new ProviderManager(
            configManager,
            Resolver(
                (ProviderTypes.OpenAi, factory.Object),
                (ProviderTypes.OpenAiResponses, factory.Object)),
            Mock.Of<IModelConfigManager>(),
            new ProviderRegistry(NullLogger<ProviderRegistry>.Instance),
            new Lazy<IModelCapabilityManager>(() => NullModelCapabilityManager.Instance),
            NullLogger<ProviderManager>.Instance);

        sut.ResolveFactory(ProviderTypes.OpenAi).Should().BeSameAs(factory.Object);
        sut.ResolveFactory(ProviderTypes.OpenAiResponses).Should().BeSameAs(factory.Object);
    }

    [Fact]
    public async Task GetClient_WithoutOpenAiFactory_AnthropicPathStillWorks()
    {
        var providers = new Dictionary<string, ProviderConfig>
        {
            ["claude"] = PredefinedProviders.Anthropic("sk-anthropic")
        };
        var configManager = await CreateConfigManagerAsync(new SeeingAgentOptions(), providers);
        var expected = Mock.Of<IChatCompletionClient>(c => c.ProviderType == ProviderTypes.Anthropic);
        var anthropic = new Mock<ILlmClientFactory>();
        anthropic.Setup(f => f.Create(It.IsAny<ProviderConfig>(), It.IsAny<LlmCapabilityKind>())).Returns(expected);

        using var sut = new ProviderManager(
            configManager,
            Resolver(anthropic.Object, ProviderTypes.Anthropic),
            Mock.Of<IModelConfigManager>(),
            new ProviderRegistry(NullLogger<ProviderRegistry>.Instance),
            new Lazy<IModelCapabilityManager>(() => NullModelCapabilityManager.Instance),
            NullLogger<ProviderManager>.Instance);

        sut.GetClient("claude", LlmCapabilityKind.Chat).Should().BeSameAs(expected);
        anthropic.Verify(f => f.Create(It.IsAny<ProviderConfig>(), It.IsAny<LlmCapabilityKind>()), Times.Once);
    }

    [Fact]
    public async Task GetClient_OpenAiAndAnthropicFactories_IndependentPaths()
    {
        var providers = new Dictionary<string, ProviderConfig>
        {
            ["openai"] = PredefinedProviders.OpenAI("sk-openai"),
            ["claude"] = PredefinedProviders.Anthropic("sk-anthropic")
        };
        var configManager = await CreateConfigManagerAsync(new SeeingAgentOptions(), providers);
        var openAiClient = Mock.Of<IChatCompletionClient>(c => c.ProviderType == ProviderTypes.OpenAi);
        var anthropicClient = Mock.Of<IChatCompletionClient>(c => c.ProviderType == ProviderTypes.Anthropic);

        var openAi = new Mock<ILlmClientFactory>();
        openAi.Setup(f => f.Create(It.IsAny<ProviderConfig>(), It.IsAny<LlmCapabilityKind>())).Returns(openAiClient);

        var anthropic = new Mock<ILlmClientFactory>();
        anthropic.Setup(f => f.Create(It.IsAny<ProviderConfig>(), It.IsAny<LlmCapabilityKind>())).Returns(anthropicClient);

        using var sut = new ProviderManager(
            configManager,
            Resolver((ProviderTypes.OpenAi, openAi.Object), (ProviderTypes.Anthropic, anthropic.Object)),
            Mock.Of<IModelConfigManager>(),
            new ProviderRegistry(NullLogger<ProviderRegistry>.Instance),
            new Lazy<IModelCapabilityManager>(() => NullModelCapabilityManager.Instance),
            NullLogger<ProviderManager>.Instance);

        sut.GetClient("openai", LlmCapabilityKind.Chat).Should().BeSameAs(openAiClient);
        sut.GetClient("claude", LlmCapabilityKind.Chat).Should().BeSameAs(anthropicClient);
        openAi.Verify(f => f.Create(It.Is<ProviderConfig>(c => c.Type == ProviderTypes.OpenAi), It.IsAny<LlmCapabilityKind>()), Times.Once);
        anthropic.Verify(f => f.Create(It.Is<ProviderConfig>(c => c.Type == ProviderTypes.Anthropic), It.IsAny<LlmCapabilityKind>()), Times.Once);
    }

    /// <summary>单个工厂 → 绑定到 openai 协议的解析器。</summary>
    private static ILlmClientFactoryResolver Resolver(ILlmClientFactory factory, string protocol = ProviderTypes.OpenAi)
        => new LlmClientFactoryResolver([new LlmClientFactoryRegistration(protocol, factory)]);

    /// <summary>多组「协议 → 工厂」→ 解析器（用于断言协议路由互不干扰）。</summary>
    private static ILlmClientFactoryResolver Resolver(
        params (string Protocol, ILlmClientFactory Factory)[] registrations)
        => new LlmClientFactoryResolver(
            registrations.Select(r => new LlmClientFactoryRegistration(r.Protocol, r.Factory)));

    private sealed class TestProvider(string id) : LlmProviderBase
    {
        public override string Id { get; } = id;

        public override string? Name => Id;

        public override ILlmCapabilityClient? GetClient(LlmCapabilityKind capability)
            => capability == LlmCapabilityKind.Chat ? Mock.Of<IChatCompletionClient>() : null;

        public override Task<IReadOnlyList<ModelConfig>> GetModelsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ModelConfig>>([]);
    }

    private sealed class DisposableClient : IChatCompletionClient, IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public string ProviderId => "provider";

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
