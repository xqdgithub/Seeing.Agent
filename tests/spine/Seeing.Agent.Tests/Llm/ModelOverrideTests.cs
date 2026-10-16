using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Llm;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace Seeing.Agent.Tests.Llm;

/// <summary>
/// 模型元数据用户覆盖层（ModelOverrides）行为测试。
/// </summary>
public sealed class ModelOverrideTests : IDisposable
{
    private readonly string _tempDirectory =
        Path.Combine(Path.GetTempPath(), "model-override-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Upsert_OverridesTypes_WithoutTouchingOtherFields()
    {
        var providers = BuildProviders();
        var configManager = await CreateConfigManagerAsync(providers);
        using var sut = new ModelConfigManager(
            configManager,
            CreateRegistry(providers),
            NullLogger<ModelConfigManager>.Instance);

        await WaitUntilAsync(
            () => sut.GetCatalogModels().ContainsKey("siliconflow/Qwen/Qwen3-Embedding-8B"),
            TimeSpan.FromSeconds(5));

        await sut.UpsertModelOverrideAsync(
            "siliconflow",
            "Qwen/Qwen3-Embedding-8B",
            new ModelOverride { Types = [ModelType.Embedding] },
            TestContext.Current.CancellationToken);

        var model = sut.GetModel("siliconflow/Qwen/Qwen3-Embedding-8B");
        model.Should().NotBeNull();
        model!.Types.Should().BeEquivalentTo(new[] { ModelType.Embedding });
        // 未被覆盖的字段保持基线（名称来自远端映射）
        model.Name.Should().Be("Qwen/Qwen3-Embedding-8B");
    }

    [Fact]
    public async Task Upsert_OnlyPersistsFieldsDifferingFromBaseline()
    {
        var providers = BuildProviders();
        var configManager = await CreateConfigManagerAsync(providers);
        using var sut = new ModelConfigManager(
            configManager,
            CreateRegistry(providers),
            NullLogger<ModelConfigManager>.Instance);

        await WaitUntilAsync(
            () => sut.GetCatalogModels().ContainsKey("siliconflow/Qwen/Qwen3-Embedding-8B"),
            TimeSpan.FromSeconds(5));

        // desired 中 name 与基线相同 → 不应固化进覆盖（远端更新后仍能跟随）。
        await sut.UpsertModelOverrideAsync(
            "siliconflow",
            "Qwen/Qwen3-Embedding-8B",
            new ModelOverride
            {
                Name = "Qwen/Qwen3-Embedding-8B",
                Types = [ModelType.Embedding]
            },
            TestContext.Current.CancellationToken);

        var patch = sut.GetModelOverride("siliconflow", "Qwen/Qwen3-Embedding-8B");
        patch.Should().NotBeNull();
        patch!.Types.Should().BeEquivalentTo(new[] { ModelType.Embedding });
        patch.Name.Should().BeNull("与基线一致的字段不应写入覆盖");

        // 持久化到用户级 model-overrides.json
        var persisted = await ReadOverridesAsync(configManager);
        persisted.Should().ContainKey("siliconflow/Qwen/Qwen3-Embedding-8B");
    }

    [Fact]
    public async Task Upsert_NewModel_AddsCatalogEntry()
    {
        var providers = BuildProviders();
        var configManager = await CreateConfigManagerAsync(providers);
        using var sut = new ModelConfigManager(
            configManager,
            CreateRegistry(providers),
            NullLogger<ModelConfigManager>.Instance);

        await WaitUntilAsync(
            () => sut.GetCatalogModels().ContainsKey("siliconflow/Qwen/Qwen3-Embedding-8B"),
            TimeSpan.FromSeconds(5));

        // 补录：目录中原本不存在。
        await sut.UpsertModelOverrideAsync(
            "siliconflow",
            "Qwen/Qwen3-New-Model",
            new ModelOverride
            {
                Name = "补录模型",
                Types = [ModelType.Text],
                ContextLimit = 128000
            },
            TestContext.Current.CancellationToken);

        var added = sut.GetModel("siliconflow/Qwen/Qwen3-New-Model");
        added.Should().NotBeNull();
        added!.Name.Should().Be("补录模型");
        added.Types.Should().BeEquivalentTo(new[] { ModelType.Text });
        added.Limit.Context.Should().Be(128000);
        added.Provider.Should().Be("siliconflow");

        var patch = sut.GetModelOverride("siliconflow", "Qwen/Qwen3-New-Model");
        patch.Should().NotBeNull();
        sut.GetModelBaseline("siliconflow", "Qwen/Qwen3-New-Model").Should().BeNull();
    }

    [Fact]
    public async Task RemoveOverride_RestoresBaseline()
    {
        var providers = BuildProviders();
        var configManager = await CreateConfigManagerAsync(providers);
        using var sut = new ModelConfigManager(
            configManager,
            CreateRegistry(providers),
            NullLogger<ModelConfigManager>.Instance);

        await WaitUntilAsync(
            () => sut.GetCatalogModels().ContainsKey("siliconflow/Qwen/Qwen3-Embedding-8B"),
            TimeSpan.FromSeconds(5));

        await sut.UpsertModelOverrideAsync(
            "siliconflow",
            "Qwen/Qwen3-Embedding-8B",
            new ModelOverride { Types = [ModelType.Embedding] },
            TestContext.Current.CancellationToken);
        sut.GetModel("siliconflow/Qwen/Qwen3-Embedding-8B")!.Types
            .Should().BeEquivalentTo(new[] { ModelType.Embedding });

        await sut.RemoveModelOverrideAsync(
            "siliconflow",
            "Qwen/Qwen3-Embedding-8B",
            TestContext.Current.CancellationToken);

        sut.GetModelOverride("siliconflow", "Qwen/Qwen3-Embedding-8B").Should().BeNull();
        // 基线（远端未返回类型 → 有效类型缺省 Text）
        sut.GetEffectiveTypes(sut.GetModel("siliconflow/Qwen/Qwen3-Embedding-8B")!)
            .Should().BeEquivalentTo(new[] { ModelType.Text });
    }

    [Fact]
    public async Task RemoveOverride_CustomModel_Disappears()
    {
        var providers = BuildProviders();
        var configManager = await CreateConfigManagerAsync(providers);
        using var sut = new ModelConfigManager(
            configManager,
            CreateRegistry(providers),
            NullLogger<ModelConfigManager>.Instance);

        await WaitUntilAsync(
            () => sut.GetCatalogModels().ContainsKey("siliconflow/Qwen/Qwen3-Embedding-8B"),
            TimeSpan.FromSeconds(5));

        await sut.UpsertModelOverrideAsync(
            "siliconflow",
            "Qwen/Qwen3-Custom",
            new ModelOverride { Types = [ModelType.Text] },
            TestContext.Current.CancellationToken);
        sut.GetCatalogModels().Should().ContainKey("siliconflow/Qwen/Qwen3-Custom");

        await sut.RemoveModelOverrideAsync(
            "siliconflow",
            "Qwen/Qwen3-Custom",
            TestContext.Current.CancellationToken);

        sut.GetCatalogModels().Should().NotContainKey("siliconflow/Qwen/Qwen3-Custom");
    }

    [Fact]
    public async Task ProviderRefresh_KeepsOverridesAndCustomEntries()
    {
        var providers = BuildProviders();
        var configManager = await CreateConfigManagerAsync(providers);
        using var sut = new ModelConfigManager(
            configManager,
            CreateRegistry(providers),
            NullLogger<ModelConfigManager>.Instance);

        await WaitUntilAsync(
            () => sut.GetCatalogModels().ContainsKey("siliconflow/Qwen/Qwen3-Embedding-8B"),
            TimeSpan.FromSeconds(5));

        await sut.UpsertModelOverrideAsync(
            "siliconflow",
            "Qwen/Qwen3-Embedding-8B",
            new ModelOverride { Types = [ModelType.Embedding] },
            TestContext.Current.CancellationToken);
        await sut.UpsertModelOverrideAsync(
            "siliconflow",
            "Qwen/Qwen3-Custom",
            new ModelOverride { Types = [ModelType.Text] },
            TestContext.Current.CancellationToken);

        // 单 Provider 刷新：覆盖与补录条目均须保留（不得被远端 slice 清掉）。
        await sut.RefreshCatalogAsync("siliconflow", TestContext.Current.CancellationToken);

        sut.GetModel("siliconflow/Qwen/Qwen3-Embedding-8B")!.Types
            .Should().BeEquivalentTo(new[] { ModelType.Embedding });
        sut.GetCatalogModels().Should().ContainKey("siliconflow/Qwen/Qwen3-Custom");
    }

    [Fact]
    public async Task ModelReloadHandler_ModelOverridesChange_RebuildsWithoutProviderRefresh()
    {
        var providers = BuildProviders();
        var configManager = await CreateConfigManagerAsync(providers);
        using var sut = new ModelConfigManager(
            configManager,
            CreateRegistry(providers),
            NullLogger<ModelConfigManager>.Instance);
        var handler = new ModelReloadHandler(sut);

        await WaitUntilAsync(
            () => sut.GetCatalogModels().ContainsKey("siliconflow/Qwen/Qwen3-Embedding-8B"),
            TimeSpan.FromSeconds(5));

        // 外部直接写覆盖节（模拟另一入口/手工编辑）。
        await configManager.SaveSectionAsync(
            "ModelOverrides",
            new Dictionary<string, ModelOverride>
            {
                ["siliconflow/Qwen/Qwen3-Embedding-8B"] = new() { Types = [ModelType.Embedding] }
            },
            ConfigLevel.User,
            TestContext.Current.CancellationToken);

        await handler.ReloadAsync(
            new ConfigChange { ChangedSections = new[] { "ModelOverrides" } },
            TestContext.Current.CancellationToken);

        sut.GetModel("siliconflow/Qwen/Qwen3-Embedding-8B")!.Types
            .Should().BeEquivalentTo(new[] { ModelType.Embedding });
    }

    [Fact]
    public async Task Upsert_UnknownProvider_Throws()
    {
        var providers = BuildProviders();
        var configManager = await CreateConfigManagerAsync(providers);
        using var sut = new ModelConfigManager(
            configManager,
            CreateRegistry(providers),
            NullLogger<ModelConfigManager>.Instance);

        var act = async () => await sut.UpsertModelOverrideAsync(
            "does-not-exist",
            "some-model",
            new ModelOverride { Types = [ModelType.Text] },
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    private static Dictionary<string, ProviderConfig> BuildProviders() => new()
    {
        ["siliconflow"] = new ProviderConfig
        {
            Id = "siliconflow",
            Type = ProviderTypes.OpenAi,
            Models = new Dictionary<string, ModelConfig>
            {
                ["Qwen/Qwen3-Embedding-8B"] = new()
                {
                    Id = "Qwen/Qwen3-Embedding-8B",
                    Name = "Qwen/Qwen3-Embedding-8B",
                    Provider = "siliconflow"
                }
            }
        }
    };

    private async Task<UnifiedConfigManager> CreateConfigManagerAsync(
        Dictionary<string, ProviderConfig> providers)
    {
        var userSeeingDirectory = Path.Combine(_tempDirectory, "user", ".seeing");
        var projectSeeingDirectory = Path.Combine(_tempDirectory, "project", ".seeing");
        Directory.CreateDirectory(userSeeingDirectory);
        Directory.CreateDirectory(projectSeeingDirectory);

        await File.WriteAllTextAsync(
            Path.Combine(userSeeingDirectory, "seeing.json"),
            JsonSerializer.Serialize(new { SeeingAgent = new SeeingAgentOptions() }));
        await File.WriteAllTextAsync(
            Path.Combine(userSeeingDirectory, "providers.json"),
            JsonSerializer.Serialize(providers));

        var workspace = new Mock<IWorkspaceProvider>();
        workspace.Setup(candidate => candidate.UserSeeingDirectory).Returns(userSeeingDirectory);
        workspace.Setup(candidate => candidate.ProjectSeeingDirectory).Returns(projectSeeingDirectory);

        var manager = new UnifiedConfigManager(
            workspace.Object,
            NullLogger<UnifiedConfigManager>.Instance,
            ConfigSectionRegistry.CreateWithSpine());
        await manager.LoadAsync();
        return manager;
    }

    private async Task<Dictionary<string, ModelOverride>> ReadOverridesAsync(UnifiedConfigManager configManager)
    {
        var path = Path.Combine(_tempDirectory, "user", ".seeing", "model-overrides.json");
        if (!File.Exists(path))
            return new Dictionary<string, ModelOverride>();

        var json = await File.ReadAllTextAsync(path);
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        return JsonSerializer.Deserialize<Dictionary<string, ModelOverride>>(json, options)
               ?? new Dictionary<string, ModelOverride>();
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

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("等待模型目录刷新超时。");

            await Task.Delay(10);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
            Directory.Delete(_tempDirectory, recursive: true);
    }
}
