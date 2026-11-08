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

    /// <summary>
    /// 单 Provider 刷新后，用户覆盖与补录条目不得被远端 slice 清掉。
    /// <para>用 <c>EnqueueProviderRefresh</c>（投递即返回）而非 <c>RefreshCatalogAsync</c>（等待完成信号）：
    /// 后者的完成信号在刷新被抢占或被丢弃时不会到来，会让测试永久挂起。
    /// 断言的是不变量（覆盖与补录仍在），不依赖刷新何时收敛。</para>
    /// </summary>
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
        // 用 EnqueueProviderRefresh 而非 RefreshCatalogAsync：后者等待「刷新完成」信号，
        // 而该信号在刷新被后续请求抢占/丢弃时会一直不来，测试会挂死。
        // 这里改为「投递 + 条件轮询」，断言的仍是不变量（覆盖与补录不丢），
        // 不依赖异步刷新何时收敛。
        var overrideStillApplied = () =>
            sut.GetModel("siliconflow/Qwen/Qwen3-Embedding-8B")?.Types
                .Contains(ModelType.Embedding) == true
            && sut.GetCatalogModels().ContainsKey("siliconflow/Qwen/Qwen3-Custom");

        sut.EnqueueProviderRefresh("test-provider-refresh", "siliconflow");

        await WaitUntilAsync(overrideStillApplied, TimeSpan.FromSeconds(5));

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
    public async Task Upsert_ThinkingOverride_ReplacesDeclaredLevels()
    {
        var providers = BuildProviders();
        var configManager = await CreateConfigManagerAsync(providers);
        using var sut = new ModelConfigManager(
            configManager,
            CreateRegistry(providers),
            NullLogger<ModelConfigManager>.Instance);

        await WaitUntilAsync(
            () => sut.GetCatalogModels().ContainsKey("siliconflow/Qwen/Qwen3-Thinking"),
            TimeSpan.FromSeconds(5));

        await sut.UpsertModelOverrideAsync(
            "siliconflow",
            "Qwen/Qwen3-Thinking",
            new ModelOverride
            {
                Thinking = new ModelThinkingOverride
                {
                    Supported = true,
                    Default = "max",
                    Levels =
                    [
                        new ThinkingLevel { Key = "off", Label = "关" },
                        new ThinkingLevel { Key = "max", Label = "最大" }
                    ]
                }
            },
            TestContext.Current.CancellationToken);

        var thinking = sut.GetModel("siliconflow/Qwen/Qwen3-Thinking")!.Options!.Thinking!;
        thinking.Supported.Should().BeTrue();
        thinking.Default.Should().Be("max");
        thinking.Levels!.Select(level => level.Key).Should().Equal("off", "max");

        // 覆盖层只落思考字段，其余字段不出现在补丁里。
        var patch = sut.GetModelOverride("siliconflow", "Qwen/Qwen3-Thinking");
        patch!.Thinking!.Supported.Should().BeTrue();
        patch.Name.Should().BeNull();
    }

    [Fact]
    public async Task Upsert_ThinkingDefaultOff_StillKeepsLevels()
    {
        var providers = BuildProviders();
        var configManager = await CreateConfigManagerAsync(providers);
        using var sut = new ModelConfigManager(
            configManager,
            CreateRegistry(providers),
            NullLogger<ModelConfigManager>.Instance);

        await WaitUntilAsync(
            () => sut.GetCatalogModels().ContainsKey("siliconflow/Qwen/Qwen3-Thinking"),
            TimeSpan.FromSeconds(5));

        var baseline = sut.GetModel("siliconflow/Qwen/Qwen3-Thinking")!.Options!.Thinking;
        await sut.UpsertModelOverrideAsync(
            "siliconflow",
            "Qwen/Qwen3-Thinking",
            new ModelOverride { Thinking = ModelThinkingOverride.CreateOff(baseline) },
            TestContext.Current.CancellationToken);

        var thinking = sut.GetModel("siliconflow/Qwen/Qwen3-Thinking")!.Options!.Thinking!;
        // 关闭必须靠「档位 + 默认档指向关闭档」表达，而不是 Supported=false：
        // 后者请求侧会清空 ThinkingEffort，OpenAI 兼容出站反而缺省开启。
        thinking.Supported.Should().BeTrue();
        ThinkingEffortKeys.IsOff(thinking.Default).Should().BeTrue("默认档必须指向关闭档");
        thinking.Levels.Should().NotBeNullOrEmpty("档位表必须保留，会话里才能手动改回其它档");

        // 出站证据：把模型默认档当作「会话播种值」显式传入（请求期不再回落默认档），
        // 规范化后 ThinkingEffort 落在关闭档 key 上。
        var request = new ChatRequest { ThinkingEffort = thinking.Default };
        LlmService.ApplyThinkingCatalog(
            sut.GetModel("siliconflow/Qwen/Qwen3-Thinking")!, request);
        request.ThinkingEffort.Should().Be(ThinkingEffortKeys.DisabledKey);
        request.ThinkingBudgetTokens.Should().BeNull();
    }

    [Fact]
    public async Task Upsert_ThinkingNotSupported_ClearsLevels()
    {
        var providers = BuildProviders();
        var configManager = await CreateConfigManagerAsync(providers);
        using var sut = new ModelConfigManager(
            configManager,
            CreateRegistry(providers),
            NullLogger<ModelConfigManager>.Instance);

        await WaitUntilAsync(
            () => sut.GetCatalogModels().ContainsKey("siliconflow/Qwen/Qwen3-Thinking"),
            TimeSpan.FromSeconds(5));

        // Supported=false = 「不声明思考能力」，与「关闭思考」不是一回事。
        await sut.UpsertModelOverrideAsync(
            "siliconflow",
            "Qwen/Qwen3-Thinking",
            new ModelOverride { Thinking = new ModelThinkingOverride { Supported = false } },
            TestContext.Current.CancellationToken);

        var thinking = sut.GetModel("siliconflow/Qwen/Qwen3-Thinking")!.Options!.Thinking!;
        thinking.Supported.Should().BeFalse();
        thinking.Levels.Should().BeNull();
        thinking.Interleaved.Should().BeNull();
    }

    [Fact]
    public async Task Upsert_ThinkingSameAsBaseline_NotPersisted()
    {
        var providers = BuildProviders();
        var configManager = await CreateConfigManagerAsync(providers);
        using var sut = new ModelConfigManager(
            configManager,
            CreateRegistry(providers),
            NullLogger<ModelConfigManager>.Instance);

        await WaitUntilAsync(
            () => sut.GetCatalogModels().ContainsKey("siliconflow/Qwen/Qwen3-Thinking"),
            TimeSpan.FromSeconds(5));

        // 与基线逐字段一致 → 不应固化进覆盖（远端刷新后仍能跟随）。
        await sut.UpsertModelOverrideAsync(
            "siliconflow",
            "Qwen/Qwen3-Thinking",
            new ModelOverride
            {
                Thinking = new ModelThinkingOverride
                {
                    Supported = true,
                    Interleaved = "reasoning_content",
                    Levels =
                    [
                        new ThinkingLevel { Key = "disabled", Label = "关闭" },
                        new ThinkingLevel { Key = "high", Label = "高" }
                    ]
                }
            },
            TestContext.Current.CancellationToken);

        sut.GetModelOverride("siliconflow", "Qwen/Qwen3-Thinking").Should().BeNull(
            "与基线完全一致的思考声明等价于无覆盖");
    }

    [Fact]
    public async Task RemoveOverride_ThinkingRestoresBaseline()
    {
        var providers = BuildProviders();
        var configManager = await CreateConfigManagerAsync(providers);
        using var sut = new ModelConfigManager(
            configManager,
            CreateRegistry(providers),
            NullLogger<ModelConfigManager>.Instance);

        await WaitUntilAsync(
            () => sut.GetCatalogModels().ContainsKey("siliconflow/Qwen/Qwen3-Thinking"),
            TimeSpan.FromSeconds(5));

        await sut.UpsertModelOverrideAsync(
            "siliconflow",
            "Qwen/Qwen3-Thinking",
            new ModelOverride { Thinking = new ModelThinkingOverride { Supported = false } },
            TestContext.Current.CancellationToken);
        sut.GetModel("siliconflow/Qwen/Qwen3-Thinking")!.Options!.Thinking!.Supported.Should().BeFalse();

        await sut.RemoveModelOverrideAsync(
            "siliconflow",
            "Qwen/Qwen3-Thinking",
            TestContext.Current.CancellationToken);

        var restored = sut.GetModel("siliconflow/Qwen/Qwen3-Thinking")!.Options!.Thinking!;
        restored.Supported.Should().BeTrue();
        restored.Levels!.Select(level => level.Key).Should().Equal("disabled", "high");
    }

    /// <summary>
    /// 覆盖应用必须深拷贝 Options：否则原地写 <c>Options.Thinking</c> 会穿透到 <c>_rawCache</c> 基线，
    /// 之后 diff 永远认为「与基线相同」，覆盖再也写不进去。
    /// </summary>
    [Fact]
    public async Task Upsert_Thinking_DoesNotMutateBaseline()
    {
        var providers = BuildProviders();
        var configManager = await CreateConfigManagerAsync(providers);
        using var sut = new ModelConfigManager(
            configManager,
            CreateRegistry(providers),
            NullLogger<ModelConfigManager>.Instance);

        await WaitUntilAsync(
            () => sut.GetCatalogModels().ContainsKey("siliconflow/Qwen/Qwen3-Thinking"),
            TimeSpan.FromSeconds(5));

        await sut.UpsertModelOverrideAsync(
            "siliconflow",
            "Qwen/Qwen3-Thinking",
            new ModelOverride
            {
                Thinking = new ModelThinkingOverride
                {
                    Supported = true,
                    Levels = [new ThinkingLevel { Key = "only", Label = "仅此一档" }]
                }
            },
            TestContext.Current.CancellationToken);

        var baseline = sut.GetModelBaseline("siliconflow", "Qwen/Qwen3-Thinking");
        // 基线必须仍是原声明的两档：覆盖写穿会让这里变成 "only"。
        baseline!.Options!.Thinking!.Levels!.Select(level => level.Key).Should().Equal("disabled", "high");
    }

    [Fact]
    public async Task Upsert_UnknownProvider_Throws()    {
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
                },
                // 已声明思考档位的模型：思考覆盖的 diff / 回退断言需要一条非空基线。
                ["Qwen/Qwen3-Thinking"] = new()
                {
                    Id = "Qwen/Qwen3-Thinking",
                    Name = "Qwen/Qwen3-Thinking",
                    Provider = "siliconflow",
                    Limit = new ModelLimits { Context = 32000, Output = 8192 },
                    Options = new ModelOptions
                    {
                        Thinking = new ThinkingOptions
                        {
                            Supported = true,
                            Interleaved = "reasoning_content",
                            Levels =
                            [
                                new ThinkingLevel { Key = "disabled", Label = "关闭" },
                                new ThinkingLevel { Key = "high", Label = "高" }
                            ]
                        }
                    }
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

            // 必须显式给出 GetModelsAsync：Moq 对未 Setup 的 Task 返回 null，
            // 而目录聚合会 await 它 → 后台刷新线程抛 NullReferenceException 后退出，
            // 初始刷新永不完成，任何等待目录就绪的断言都会挂死。
            provider.Setup(candidate => candidate.GetModelsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyList<ModelConfig>)Array.Empty<ModelConfig>());

            registry.Register(new ProviderRegistration(provider.Object, ProviderOrigin.Configured));
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
