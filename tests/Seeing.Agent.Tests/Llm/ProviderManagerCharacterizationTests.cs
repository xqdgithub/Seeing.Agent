using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Configuration;
using Seeing.Agent.Llm;
using Seeing.Agent.Abstractions.Llm;
using Xunit;

// Characterization classification:
// - Expected behavior changes:
//   GetProviders_ReturnsConfiguredProviderInfo,
//   TestConnectionAsync_ConfigChanged_RecreatesClientOnProviderSettingsChange.
// - Locked legacy behavior:
//   GetClient_ConfiguredProvider_ReturnsClient,
//   GetClient_UnknownProvider_ReturnsNull.
namespace Seeing.Agent.Tests.Llm;

public class ProviderManagerCharacterizationTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _tempDirectory =
        Path.Combine(Path.GetTempPath(), "provider-manager-characterization-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task GetClient_ConfiguredProvider_ReturnsClient()
    {
        var providers = new Dictionary<string, ProviderConfig>
        {
            ["openai"] = PredefinedProviders.OpenAI("sk-test")
        };
        var configManager = await CreateConfigManagerAsync(new SeeingAgentOptions(), providers);
        var expectedClient = CreateClient(ProviderType.OpenAI);
        var factory = CreateFactory((_) => expectedClient);
        using var sut = new ProviderManager(
            configManager,
            factory.Object,
            Mock.Of<IModelConfigManager>(),
            new ProviderRegistry(NullLogger<ProviderRegistry>.Instance),
            NullLogger<ProviderManager>.Instance);

        var client = sut.GetClient("openai");

        client.Should().BeSameAs(expectedClient);
        client!.ProviderType.Should().Be(ProviderType.OpenAI);
    }

    [Fact]
    public async Task GetClient_UnknownProvider_ReturnsNull()
    {
        var providers = new Dictionary<string, ProviderConfig>
        {
            ["openai"] = PredefinedProviders.OpenAI("sk-test")
        };
        var configManager = await CreateConfigManagerAsync(new SeeingAgentOptions(), providers);
        var factory = CreateFactory((_) => CreateClient(ProviderType.OpenAI));
        using var sut = new ProviderManager(
            configManager,
            factory.Object,
            Mock.Of<IModelConfigManager>(),
            new ProviderRegistry(NullLogger<ProviderRegistry>.Instance),
            NullLogger<ProviderManager>.Instance);

        var client = sut.GetClient("unknown");

        client.Should().BeNull();
    }

    [Fact]
    // 预期行为变更：公开 API 现在返回 ProviderInfo，而非 ProviderConfig。
    public async Task GetProviders_ReturnsConfiguredProviderInfo()
    {
        var openAi = PredefinedProviders.OpenAI("sk-test");
        var anthropic = PredefinedProviders.Anthropic("sk-anthropic-test");
        var configuredProviders = new Dictionary<string, ProviderConfig>
        {
            ["openai"] = openAi,
            ["anthropic"] = anthropic
        };
        var configManager = await CreateConfigManagerAsync(new SeeingAgentOptions(), configuredProviders);
        var factory = CreateFactory(config => CreateClient(config.Type));
        using var sut = new ProviderManager(
            configManager,
            factory.Object,
            Mock.Of<IModelConfigManager>(),
            new ProviderRegistry(NullLogger<ProviderRegistry>.Instance),
            NullLogger<ProviderManager>.Instance);

        var providers = sut.GetProviders();

        providers.Keys.Should().BeEquivalentTo("openai", "anthropic");
        providers["openai"].Name.Should().Be("OpenAI");
        providers["openai"].Source.Should().Be(ProviderSource.Configured);
        providers["anthropic"].MaxRetries.Should().Be(3);
    }

    [Fact]
    // 预期行为变更：同类型下的 API Key 更改也必须重建配置驱动 Provider。
    public async Task TestConnectionAsync_ConfigChanged_RecreatesClientOnProviderSettingsChange()
    {
        var providers = new Dictionary<string, ProviderConfig>
        {
            ["provider"] = PredefinedProviders.OpenAI("sk-original")
        };
        var configManager = await CreateConfigManagerAsync(new SeeingAgentOptions(), providers);
        var openAiClient = CreateClient(ProviderType.OpenAI, connectionResult: false);
        var anthropicClient = CreateClient(ProviderType.Anthropic, connectionResult: true);
        var factory = CreateFactory(config =>
            config.Type == ProviderType.OpenAI ? openAiClient : anthropicClient);
        using var sut = new ProviderManager(
            configManager,
            factory.Object,
            Mock.Of<IModelConfigManager>(),
            new ProviderRegistry(NullLogger<ProviderRegistry>.Instance),
            NullLogger<ProviderManager>.Instance);

        sut.GetClient("provider").Should().BeSameAs(openAiClient);

        await sut.SaveProviderAsync(
            "provider",
            new ProviderConfig
            {
                Id = "provider",
                Type = ProviderType.OpenAI,
                ApiKey = "sk-changed"
            },
            ct: TestContext.Current.CancellationToken);
        var clientAfterSameTypeChange = sut.GetClient("provider");
        var sameTypeConnectionResult = await sut.TestConnectionAsync(
            "provider",
            "model",
            TestContext.Current.CancellationToken);

        clientAfterSameTypeChange.Should().BeSameAs(openAiClient);
        sameTypeConnectionResult.Should().BeFalse();
        factory.Verify(candidate => candidate.Create(It.IsAny<ProviderConfig>()), Times.Exactly(2));

        await sut.SaveProviderAsync(
            "provider",
            new ProviderConfig
            {
                Id = "provider",
                Type = ProviderType.Anthropic,
                ApiKey = "sk-anthropic"
            },
            ct: TestContext.Current.CancellationToken);
        var clientAfterTypeChange = sut.GetClient("provider");
        var changedTypeConnectionResult = await sut.TestConnectionAsync(
            "provider",
            "model",
            TestContext.Current.CancellationToken);

        clientAfterTypeChange.Should().BeSameAs(anthropicClient);
        changedTypeConnectionResult.Should().BeTrue();
        factory.Verify(candidate => candidate.Create(It.IsAny<ProviderConfig>()), Times.Exactly(3));
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
        await File.WriteAllTextAsync(Path.Combine(projectSeeingDirectory, "seeing.json"), json);

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

    private static Mock<ILlmClientFactory> CreateFactory(Func<ProviderConfig, ILlmClient> createClient)
    {
        var factory = new Mock<ILlmClientFactory>();
        factory.Setup(candidate => candidate.SupportsType(It.IsAny<ProviderType>())).Returns(true);
        factory.Setup(candidate => candidate.Create(It.IsAny<ProviderConfig>()))
            .Returns((ProviderConfig config) => createClient(config));
        return factory;
    }

    private static ILlmClient CreateClient(ProviderType providerType, bool connectionResult = true)
    {
        var client = new Mock<ILlmClient>();
        client.SetupGet(candidate => candidate.ProviderType).Returns(providerType);
        client.Setup(candidate => candidate.TestConnectionAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(connectionResult);
        return client.Object;
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
            Directory.Delete(_tempDirectory, recursive: true);
    }
}
