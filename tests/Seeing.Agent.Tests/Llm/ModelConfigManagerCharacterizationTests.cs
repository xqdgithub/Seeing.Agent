using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Configuration;
using Seeing.Agent.Llm;
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
        var options = new SeeingAgentOptions
        {
            DefaultProvider = "openai",
            Providers =
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
            }
        };
        var configManager = await CreateConfigManagerAsync(options);
        using var sut = new ModelConfigManager(
            configManager,
            CreateRegistry(options),
            NullLogger<ModelConfigManager>.Instance);

        var models = sut.GetModels();

        models.Keys.Should().BeEquivalentTo("openai/provider-model");
        models["openai/provider-model"].Provider.Should().Be("openai");
    }

    [Fact]
    public async Task GetModel_BareId_FallsBackToAnyProvider()
    {
        var providerModel = new ModelConfig { Id = "provider-only-model" };
        var options = new SeeingAgentOptions
        {
            Providers =
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
            }
        };
        var configManager = await CreateConfigManagerAsync(options);
        using var sut = new ModelConfigManager(
            configManager,
            CreateRegistry(options),
            NullLogger<ModelConfigManager>.Instance);

        var model = sut.GetModel("provider-only-model");

        model.Should().NotBeNull();
        model!.Id.Should().Be("provider-only-model");
        model.Provider.Should().Be("openai");
    }

    [Fact]
    public async Task AddModelAsync_WritesToUserProvidersModels()
    {
        var configManager = await CreateConfigManagerAsync(new SeeingAgentOptions
        {
            Providers =
            {
                ["openai"] = new ProviderConfig
                {
                    Id = "openai",
                    Type = ProviderType.OpenAI
                }
            }
        });
        using var sut = new ModelConfigManager(
            configManager,
            CreateRegistry(configManager.SeeingAgent),
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

        configManager.UserSeeingAgent!.Providers["openai"].Models.Should().ContainKey("added-model");
        using var document = await ReadUserConfigAsync();
        document.RootElement.GetProperty("SeeingAgent")
            .GetProperty("Providers")
            .GetProperty("openai")
            .GetProperty("models")
            .TryGetProperty("added-model", out _)
            .Should().BeTrue();

        await WaitUntilAsync(
            () => sut.GetModels().ContainsKey("openai/added-model"),
            TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Load_AbsorbsProjectProviderModelsBeforeRemovingProviders()
    {
        var userSeeingDirectory = Path.Combine(_tempDirectory, "user", ".seeing");
        var projectSeeingDirectory = Path.Combine(_tempDirectory, "project", ".seeing");
        Directory.CreateDirectory(userSeeingDirectory);
        Directory.CreateDirectory(projectSeeingDirectory);

        // 用户已有同名 Provider（无 ApiKey）；项目级同时有 Providers + 误写的 ProviderModels
        await File.WriteAllTextAsync(
            Path.Combine(userSeeingDirectory, "seeing.json"),
            """
            {
              "SeeingAgent": {
                "Providers": {
                  "siliconflow": {
                    "id": "siliconflow",
                    "type": "OpenAI",
                    "name": "siliconflow"
                  }
                }
              }
            }
            """);
        await File.WriteAllTextAsync(
            Path.Combine(projectSeeingDirectory, "seeing.json"),
            """
            {
              "SeeingAgent": {
                "Providers": {
                  "siliconflow": {
                    "id": "siliconflow",
                    "type": "OpenAI",
                    "apiKey": "sk-project",
                    "baseURL": "https://api.siliconflow.cn/v1/",
                    "models": {
                      "from-providers": { "id": "from-providers", "provider": "siliconflow" }
                    }
                  }
                },
                "ProviderModels": {
                  "siliconflow": {
                    "from-provider-models": { "id": "from-provider-models", "provider": "siliconflow" }
                  }
                }
              }
            }
            """);

        var workspace = new Mock<IWorkspaceProvider>();
        workspace.Setup(candidate => candidate.WorkspaceRoot).Returns(Path.Combine(_tempDirectory, "project"));
        workspace.Setup(candidate => candidate.UserSeeingDirectory).Returns(userSeeingDirectory);
        workspace.Setup(candidate => candidate.ProjectSeeingDirectory).Returns(projectSeeingDirectory);

        var manager = new UnifiedConfigManager(
            workspace.Object,
            NullLogger<UnifiedConfigManager>.Instance);
        await manager.LoadAsync();

        var provider = manager.SeeingAgent.Providers["siliconflow"];
        provider.ApiKey.Should().Be("sk-project");
        provider.BaseUrl.Should().Be("https://api.siliconflow.cn/v1/");
        provider.Models.Should().ContainKey("from-providers");
        provider.Models.Should().ContainKey("from-provider-models");

        using var projectDoc = await ReadProjectConfigAsync();
        projectDoc.RootElement.GetProperty("SeeingAgent")
            .TryGetProperty("Providers", out _).Should().BeFalse();
        projectDoc.RootElement.GetProperty("SeeingAgent")
            .TryGetProperty("ProviderModels", out _).Should().BeFalse();
    }

    [Fact]
    public async Task AddModelAsync_ProjectProviders_MigratedToUserOnly()
    {
        var provider = new ProviderConfig
        {
            Id = "siliconflow",
            Type = ProviderType.OpenAI,
            Name = "siliconflow",
            BaseUrl = "https://api.siliconflow.cn/v1/"
        };
        var configManager = await CreateDualLevelConfigManagerAsync(
            userOptions: new SeeingAgentOptions(),
            projectOptions: new SeeingAgentOptions { Providers = { ["siliconflow"] = CloneForTest(provider) } });
        using var sut = new ModelConfigManager(
            configManager,
            CreateRegistry(configManager.SeeingAgent),
            NullLogger<ModelConfigManager>.Instance);

        configManager.SeeingAgent.Providers.Should().ContainKey("siliconflow");

        await sut.AddModelAsync(
            "siliconflow/Qwen/Qwen3-8B",
            new ModelConfig
            {
                Id = "Qwen/Qwen3-8B",
                Provider = "siliconflow",
                Name = "Qwen3-8B"
            },
            ConfigLevel.User,
            TestContext.Current.CancellationToken);

        configManager.UserSeeingAgent!.Providers["siliconflow"].Models.Should()
            .ContainKey("Qwen/Qwen3-8B");

        using var userDoc = await ReadUserConfigAsync();
        userDoc.RootElement.GetProperty("SeeingAgent")
            .GetProperty("Providers")
            .GetProperty("siliconflow")
            .GetProperty("models")
            .TryGetProperty("Qwen/Qwen3-8B", out _)
            .Should().BeTrue();

        using var projectDoc = await ReadProjectConfigAsync();
        projectDoc.RootElement.GetProperty("SeeingAgent")
            .TryGetProperty("Providers", out _)
            .Should().BeFalse("Providers 为 UserOnly，项目级不应保留");

        await WaitUntilAsync(
            () => sut.GetModels().ContainsKey("siliconflow/Qwen/Qwen3-8B"),
            TimeSpan.FromSeconds(5));
    }

    private static ProviderConfig CloneForTest(ProviderConfig config) => new()
    {
        Id = config.Id,
        Type = config.Type,
        Name = config.Name,
        BaseUrl = config.BaseUrl,
        ApiKey = config.ApiKey,
        Timeout = config.Timeout,
        MaxRetries = config.MaxRetries
    };

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

    private async Task<UnifiedConfigManager> CreateDualLevelConfigManagerAsync(
        SeeingAgentOptions userOptions,
        SeeingAgentOptions projectOptions)
    {
        var userSeeingDirectory = Path.Combine(_tempDirectory, "user", ".seeing");
        var projectSeeingDirectory = Path.Combine(_tempDirectory, "project", ".seeing");
        Directory.CreateDirectory(userSeeingDirectory);
        Directory.CreateDirectory(projectSeeingDirectory);

        await File.WriteAllTextAsync(
            Path.Combine(userSeeingDirectory, "seeing.json"),
            JsonSerializer.Serialize(new { SeeingAgent = userOptions }, JsonOptions));
        await File.WriteAllTextAsync(
            Path.Combine(projectSeeingDirectory, "seeing.json"),
            JsonSerializer.Serialize(new { SeeingAgent = projectOptions }, JsonOptions));

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

    [Fact]
    public async Task DeleteModelAsync_RemovesFromUserProvidersModels()
    {
        var options = new SeeingAgentOptions
        {
            Providers =
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
            }
        };
        var configManager = await CreateConfigManagerAsync(options);
        using var sut = new ModelConfigManager(
            configManager,
            CreateRegistry(configManager.SeeingAgent),
            NullLogger<ModelConfigManager>.Instance);

        await sut.DeleteModelAsync(
            "remove-model",
            ct: TestContext.Current.CancellationToken);

        configManager.UserSeeingAgent!.Providers["openai"].Models.Should().ContainKey("keep-model")
            .And.NotContainKey("remove-model");
        using var document = await ReadUserConfigAsync();
        var models = document.RootElement
            .GetProperty("SeeingAgent")
            .GetProperty("Providers")
            .GetProperty("openai")
            .GetProperty("models");
        models.TryGetProperty("keep-model", out _).Should().BeTrue();
        models.TryGetProperty("remove-model", out _).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteModelAsync_CleansProjectLevelResidualProviders()
    {
        // 场景：项目级 .seeing/seeing.json 残留 Providers（历史/外部写回），
        // 删除模型必须同步清理项目级残留，否则下次加载被吸收合并导致"删除复活"。
        var userSeeingDirectory = Path.Combine(_tempDirectory, "user", ".seeing");
        var projectSeeingDirectory = Path.Combine(_tempDirectory, "project", ".seeing");
        Directory.CreateDirectory(userSeeingDirectory);
        Directory.CreateDirectory(projectSeeingDirectory);

        await File.WriteAllTextAsync(
            Path.Combine(userSeeingDirectory, "seeing.json"),
            """
            {
              "SeeingAgent": {
                "Providers": {
                  "openai": {
                    "id": "openai",
                    "type": "OpenAI",
                    "models": {
                      "keep-model": { "id": "keep-model", "provider": "openai" },
                      "remove-model": { "id": "remove-model", "provider": "openai" }
                    }
                  }
                }
              }
            }
            """);
        var projectResidual =
            """
            {
              "SeeingAgent": {
                "Providers": {
                  "openai": {
                    "id": "openai",
                    "type": "OpenAI",
                    "models": {
                      "remove-model": { "id": "remove-model", "provider": "openai" }
                    }
                  }
                }
              }
            }
            """;
        await File.WriteAllTextAsync(
            Path.Combine(projectSeeingDirectory, "seeing.json"),
            projectResidual);

        var workspace = new Mock<IWorkspaceProvider>();
        workspace.Setup(candidate => candidate.WorkspaceRoot).Returns(Path.Combine(_tempDirectory, "project"));
        workspace.Setup(candidate => candidate.UserSeeingDirectory).Returns(userSeeingDirectory);
        workspace.Setup(candidate => candidate.ProjectSeeingDirectory).Returns(projectSeeingDirectory);

        var configManager = new UnifiedConfigManager(
            workspace.Object,
            NullLogger<UnifiedConfigManager>.Instance);
        await configManager.LoadAsync();

        // 模拟加载后项目级文件被外部重新写入残留（如 git 恢复 / 旧版本进程）
        await File.WriteAllTextAsync(
            Path.Combine(projectSeeingDirectory, "seeing.json"),
            projectResidual);

        using var sut = new ModelConfigManager(
            configManager,
            CreateRegistry(configManager.SeeingAgent),
            NullLogger<ModelConfigManager>.Instance);

        await sut.DeleteModelAsync(
            "remove-model",
            ct: TestContext.Current.CancellationToken);

        // 删除后：用户级文件移除模型，且项目级残留 Providers 键被清理
        using (var document = await ReadUserConfigAsync())
        {
            var models = document.RootElement
                .GetProperty("SeeingAgent")
                .GetProperty("Providers")
                .GetProperty("openai")
                .GetProperty("models");
            models.TryGetProperty("keep-model", out _).Should().BeTrue();
            models.TryGetProperty("remove-model", out _).Should().BeFalse();
        }

        var projectJson = await File.ReadAllTextAsync(
            Path.Combine(projectSeeingDirectory, "seeing.json"));
        projectJson.Contains("\"Providers\"").Should().BeFalse("删除后必须清理项目级 Providers 残留");
        projectJson.Contains("\"ProviderModels\"").Should().BeFalse("删除后必须清理项目级 ProviderModels 残留");

        // 模拟重启：重新加载后模型不应复活
        var configManager2 = new UnifiedConfigManager(
            workspace.Object,
            NullLogger<UnifiedConfigManager>.Instance);
        await configManager2.LoadAsync(TestContext.Current.CancellationToken);
        using var sut2 = new ModelConfigManager(
            configManager2,
            CreateRegistry(configManager2.SeeingAgent),
            NullLogger<ModelConfigManager>.Instance);

        sut2.GetModels().Keys.Should().Contain("openai/keep-model");
        sut2.GetModels().Keys.Should().NotContain("openai/remove-model");
    }

    private async Task<UnifiedConfigManager> CreateConfigManagerAsync(SeeingAgentOptions options)
    {
        var userSeeingDirectory = Path.Combine(_tempDirectory, "user", ".seeing");
        var projectSeeingDirectory = Path.Combine(_tempDirectory, "project", ".seeing");
        Directory.CreateDirectory(userSeeingDirectory);
        Directory.CreateDirectory(projectSeeingDirectory);

        var json = JsonSerializer.Serialize(new { SeeingAgent = options }, JsonOptions);
        await File.WriteAllTextAsync(Path.Combine(userSeeingDirectory, "seeing.json"), json);

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

    private static IProviderRegistry CreateRegistry(SeeingAgentOptions options)
    {
        var registry = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance);
        foreach (var providerId in options.Providers.Keys)
        {
            var provider = new Mock<ILlmProvider>();
            provider.SetupGet(candidate => candidate.Id).Returns(providerId);
            registry.Register(provider.Object);
        }

        return registry;
    }

    private async Task<JsonDocument> ReadProjectConfigAsync()
    {
        var path = Path.Combine(_tempDirectory, "project", ".seeing", "seeing.json");
        await using var stream = File.OpenRead(path);
        return await JsonDocument.ParseAsync(stream);
    }

    private async Task<JsonDocument> ReadUserConfigAsync()
    {
        var path = Path.Combine(_tempDirectory, "user", ".seeing", "seeing.json");
        await using var stream = File.OpenRead(path);
        return await JsonDocument.ParseAsync(stream);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
            Directory.Delete(_tempDirectory, recursive: true);
    }
}
