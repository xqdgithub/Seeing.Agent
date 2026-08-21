using Seeing.Agent.Abstractions.Configuration;
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
//   GetModels_ReturnsProvidersModelsOnly,
//   AddModelAsync_WritesToUserProvidersModels,
//   DeleteModelAsync_RemovesFromUserProvidersModels.
// - Locked legacy behavior:
//   GetModel_BareId_FallsBackToAnyProvider.
namespace Seeing.Agent.Tests.Llm;

public class ModelConfigManagerCharacterizationTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _tempDirectory =
        Path.Combine(Path.GetTempPath(), "model-config-manager-characterization-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task GetModels_ReturnsProvidersModelsOnly()
    {
        var providerModel = new ModelConfig { Id = "provider-model" };
        var providers = new Dictionary<string, ProviderConfig>
        {
            ["openai"] = new ProviderConfig
            {
                Id = "openai",
                Type = ProviderType.OpenAI,
                Models = new Dictionary<string, ModelConfig>
                {
                    ["provider-model"] = providerModel
                }
            }
        };
        var configManager = await CreateConfigManagerAsync(new SeeingAgentOptions(), providers);
        using var sut = new ModelConfigManager(
            configManager,
            CreateRegistry(providers),
            NullLogger<ModelConfigManager>.Instance);

        var models = sut.GetModels();

        models.Keys.Should().BeEquivalentTo("openai/provider-model");
        models["openai/provider-model"].Provider.Should().Be("openai");
    }

    [Fact]
    public async Task GetModels_PreservesMetadataOnClone()
    {
        // 配置驱动的模型经过 CloneModelConfig 克隆，扩展元数据（如 isFree）必须保留
        var providerModel = new ModelConfig
        {
            Id = "free-model",
            Metadata = new Dictionary<string, object?>
            {
                [ModelMetadataKeys.IsFree] = true
            }
        };
        var providers = new Dictionary<string, ProviderConfig>
        {
            ["opencode-zen"] = new ProviderConfig
            {
                Id = "opencode-zen",
                Type = ProviderType.OpenAI,
                Models = new Dictionary<string, ModelConfig>
                {
                    ["free-model"] = providerModel
                }
            }
        };
        var configManager = await CreateConfigManagerAsync(new SeeingAgentOptions(), providers);
        using var sut = new ModelConfigManager(
            configManager,
            CreateRegistry(providers),
            NullLogger<ModelConfigManager>.Instance);

        var cached = sut.GetModel("opencode-zen/free-model");

        cached.Should().NotBeNull();
        cached!.Metadata.Should().ContainKey(ModelMetadataKeys.IsFree);

        // JSON round-trip 后值为 JsonElement；统一按 truthy 校验
        var freeValue = cached.Metadata![ModelMetadataKeys.IsFree];
        if (freeValue is JsonElement element)
            element.ValueKind.Should().Be(JsonValueKind.True);
        else
            freeValue.Should().Be(true);
    }

    [Fact]
    public async Task GetModel_BareId_FallsBackToAnyProvider()
    {
        var providerModel = new ModelConfig { Id = "provider-only-model" };
        var providers = new Dictionary<string, ProviderConfig>
        {
            ["openai"] = new ProviderConfig
            {
                Id = "openai",
                Type = ProviderType.OpenAI,
                Models = new Dictionary<string, ModelConfig>
                {
                    ["provider-only-model"] = providerModel
                }
            }
        };
        var configManager = await CreateConfigManagerAsync(new SeeingAgentOptions(), providers);
        using var sut = new ModelConfigManager(
            configManager,
            CreateRegistry(providers),
            NullLogger<ModelConfigManager>.Instance);

        var model = sut.GetModel("provider-only-model");

        model.Should().NotBeNull();
        model!.Id.Should().Be("provider-only-model");
        model.Provider.Should().Be("openai");
    }

    [Fact]
    public async Task AddModelAsync_WritesToUserProvidersModels()
    {
        var providers = new Dictionary<string, ProviderConfig>
        {
            ["openai"] = new ProviderConfig
            {
                Id = "openai",
                Type = ProviderType.OpenAI
            }
        };
        var configManager = await CreateConfigManagerAsync(new SeeingAgentOptions(), providers);
        using var sut = new ModelConfigManager(
            configManager,
            CreateRegistry(providers),
            NullLogger<ModelConfigManager>.Instance);
        var model = new ModelConfig
        {
            Id = "added-model",
            Provider = "openai"
        };

        await sut.AddModelAsync(
            "added-model",
            model,
            ConfigLevel.Project, // 请求级被忽略，固定写用户级
            TestContext.Current.CancellationToken);

        configManager.GetSection<Dictionary<string, ProviderConfig>>("Providers")["openai"].Models.Should().ContainKey("added-model");
        using var document = await ReadUserConfigAsync();
        document.RootElement
            .GetProperty("openai")
            .GetProperty("models")
            .TryGetProperty("added-model", out _)
            .Should().BeTrue();

        // Manager 不再自订阅 ConfigChanged，需通过 ReloadHandler 触发目录刷新
        var handler = new ModelReloadHandler(sut);
        await handler.ReloadAsync(
            new ConfigChange { ChangedSections = new[] { "Providers" } },
            TestContext.Current.CancellationToken);

        await WaitUntilAsync(
            () => sut.GetModels().ContainsKey("openai/added-model"),
            TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ModelReloadHandler_配置变更入队刷新()
    {
        // Arrange: 构造 manager（复用现有基建）
        var providerModel = new ModelConfig { Id = "initial-model" };
        var providers = new Dictionary<string, ProviderConfig>
        {
            ["openai"] = new ProviderConfig
            {
                Id = "openai",
                Type = ProviderType.OpenAI,
                Models = new Dictionary<string, ModelConfig>
                {
                    ["initial-model"] = providerModel
                }
            }
        };
        var configManager = await CreateConfigManagerAsync(new SeeingAgentOptions(), providers);
        using var sut = new ModelConfigManager(
            configManager,
            CreateRegistry(providers),
            NullLogger<ModelConfigManager>.Instance);
        var handler = new ModelReloadHandler(sut);

        // 等待初始目录刷新完成，确保后续断言不受构造时 EnqueueRefresh 干扰
        await WaitUntilAsync(
            () => sut.GetModels().ContainsKey("openai/initial-model"),
            TimeSpan.FromSeconds(5));

        // 外部修改配置：新增一个模型（Manager 不再自订阅 ConfigChanged，缓存保持旧目录）
        var updated = new Dictionary<string, ProviderConfig>
        {
            ["openai"] = new ProviderConfig
            {
                Id = "openai",
                Type = ProviderType.OpenAI,
                Models = new Dictionary<string, ModelConfig>
                {
                    ["initial-model"] = providerModel,
                    ["added-model"] = new ModelConfig { Id = "added-model" }
                }
            }
        };
        await configManager.SaveSectionAsync(
            "Providers", updated, ConfigLevel.User, TestContext.Current.CancellationToken);

        // Act: 调 handler.ReloadAsync 触发刷新
        await handler.ReloadAsync(
            new ConfigChange { ChangedSections = new[] { "Providers" } },
            TestContext.Current.CancellationToken);

        // Assert: 队列收到刷新请求，模型目录包含新增模型
        await WaitUntilAsync(
            () => sut.GetModels().ContainsKey("openai/added-model"),
            TimeSpan.FromSeconds(5));
    }

    private async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var start = DateTime.UtcNow;
        while (DateTime.UtcNow - start < timeout)
        {
            if (condition())
                return;
            await Task.Delay(20);
        }

        condition().Should().BeTrue("timed out waiting for condition");
    }

    [Fact]
    public async Task DeleteModelAsync_RemovesFromUserProvidersModels()
    {
        var providers = new Dictionary<string, ProviderConfig>
        {
            ["openai"] = new ProviderConfig
            {
                Id = "openai",
                Type = ProviderType.OpenAI,
                Models = new Dictionary<string, ModelConfig>
                {
                    ["keep-model"] = new() { Id = "keep-model", Provider = "openai" },
                    ["remove-model"] = new() { Id = "remove-model", Provider = "openai" }
                }
            }
        };
        var configManager = await CreateConfigManagerAsync(new SeeingAgentOptions(), providers);
        using var sut = new ModelConfigManager(
            configManager,
            CreateRegistry(providers),
            NullLogger<ModelConfigManager>.Instance);

        await sut.DeleteModelAsync(
            "remove-model",
            ct: TestContext.Current.CancellationToken);

        configManager.GetSection<Dictionary<string, ProviderConfig>>("Providers")["openai"].Models.Should().ContainKey("keep-model")
            .And.NotContainKey("remove-model");
        using var document = await ReadUserConfigAsync();
        var models = document.RootElement
            .GetProperty("openai")
            .GetProperty("models");
        models.TryGetProperty("keep-model", out _).Should().BeTrue();
        models.TryGetProperty("remove-model", out _).Should().BeFalse();
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

    private static IProviderRegistry CreateRegistry(Dictionary<string, ProviderConfig> providers)
    {
        var registry = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance);
        foreach (var providerId in providers.Keys)
        {
            var provider = new Mock<ILlmProvider>();
            provider.SetupGet(candidate => candidate.Id).Returns(providerId);
            registry.Register(provider.Object);
        }

        return registry;
    }

    private async Task<JsonDocument> ReadUserConfigAsync()
    {
        var path = Path.Combine(_tempDirectory, "user", ".seeing", "providers.json");
        await using var stream = File.OpenRead(path);
        return await JsonDocument.ParseAsync(stream);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
            Directory.Delete(_tempDirectory, recursive: true);
    }
}
