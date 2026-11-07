using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Llm;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Tests.Llm;

public sealed class ModelCatalogAggregationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "model-catalog-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task GetModels_ExtensionProviderRegisteredBeforeConstruction_VisibleAfterInitialRefresh()
    {
        var config = await CreateConfigAsync(new SeeingAgentOptions());
        var registry = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance);
        registry.Register(new ProviderRegistration(new TestProvider("extension", [new() { Id = "initial-model" }]), ProviderOrigin.Extension, OwnerId: "sample-extension"));
        using var catalog = new ModelConfigManager(
            config,
            registry,
            NullLogger<ModelConfigManager>.Instance);

        await WaitUntilAsync(
            () => catalog.GetCatalogModels().ContainsKey("extension/initial-model"),
            TimeSpan.FromSeconds(5));

        catalog.GetModel("extension/initial-model")!.Provider.Should().Be("extension");
    }

    [Fact]
    public async Task GetModels_ExtensionProvider_DynamicModelsVisibleAfterRefresh()
    {
        var providers = new Dictionary<string, ProviderConfig>
        {
            ["openai"] = new ProviderConfig
            {
                Id = "openai",
                Models = new Dictionary<string, ModelConfig>
                {
                    ["configured"] = new() { Id = "configured" }
                }
            }
        };
        var config = await CreateConfigAsync(new SeeingAgentOptions(), providers);
        var registry = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance);
        registry.Register(new ProviderRegistration(new TestProvider("openai", [new() { Id = "configured" }]), ProviderOrigin.Configured));
        using var catalog = new ModelConfigManager(
            config,
            registry,
            NullLogger<ModelConfigManager>.Instance);
        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        catalog.ModelConfigChanged += (_, _) => refreshed.TrySetResult();

        registry.Register(new ProviderRegistration(new TestProvider("extension", [new() { Id = "dynamic-model" }]), ProviderOrigin.Extension, OwnerId: "sample-extension"));

        await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        catalog.GetCatalogModels().Keys.Should().BeEquivalentTo(
            "openai/configured",
            "extension/dynamic-model");
        catalog.GetModel("extension/dynamic-model")!.Provider.Should().Be("extension");
    }

    [Fact]
    public async Task GetModels_OneProviderThrows_OthersStillVisible()
    {
        var config = await CreateConfigAsync(new SeeingAgentOptions());
        var registry = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance);
        registry.Register(new ProviderRegistration(new TestProvider("healthy", [new() { Id = "visible" }]), ProviderOrigin.Extension, OwnerId: "ext"));
        registry.Register(new ProviderRegistration(new TestProvider("failing", error: new InvalidOperationException("expected")), ProviderOrigin.Extension, OwnerId: "ext"));
        using var catalog = new ModelConfigManager(
            config,
            registry,
            NullLogger<ModelConfigManager>.Instance);
        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        catalog.ModelConfigChanged += (_, _) => refreshed.TrySetResult();

        registry.Register(new ProviderRegistration(new TestProvider("trigger", []), ProviderOrigin.Extension, OwnerId: "ext"));

        // 事件在缓存刷新完成后发出，可直接作为同步点断言：单个 Provider 抛异常须被隔离，
        // 且按 Provider 粒度的刷新不得使在途全量刷新作废而丢失其他 Provider 的模型。
        await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        catalog.GetCatalogModels().Keys.Should().ContainSingle().Which.Should().Be("healthy/visible");
    }

    [Fact]
    public async Task AddModelAsync_ExtensionProvider_DoesNotPersistConfiguration()
    {
        var config = await CreateConfigAsync(new SeeingAgentOptions());
        var registry = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance);
        registry.Register(new ProviderRegistration(new TestProvider("extension"), ProviderOrigin.Extension, OwnerId: "sample-extension"));
        using var catalog = new ModelConfigManager(
            config,
            registry,
            NullLogger<ModelConfigManager>.Instance);

        await catalog.AddModelAsync(
            "extension/dynamic",
            new ModelConfig { Id = "dynamic", Provider = "extension" }, ct: TestContext.Current.CancellationToken);

        config.GetSection<Dictionary<string, ProviderConfig>>("Providers").Should().BeEmpty();
    }

    [Fact]
    public async Task SaveModelsAsync_ExtensionProvider_DoesNotPersistConfiguration()
    {
        var config = await CreateConfigAsync(new SeeingAgentOptions());
        var registry = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance);
        registry.Register(new ProviderRegistration(new TestProvider("extension"), ProviderOrigin.Extension, OwnerId: "sample-extension"));
        using var catalog = new ModelConfigManager(
            config,
            registry,
            NullLogger<ModelConfigManager>.Instance);

        await catalog.SaveModelsAsync(
            "extension",
            new Dictionary<string, ModelConfig>
            {
                ["dynamic"] = new() { Id = "dynamic" }
            }, ct: TestContext.Current.CancellationToken);

        config.GetSection<Dictionary<string, ProviderConfig>>("Providers").Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateModelAsync_ExistingCatalogOwnerWinsOverReplacementProvider()
    {
        var providers = new Dictionary<string, ProviderConfig>
        {
            ["openai"] = new ProviderConfig
            {
                Id = "openai",
                Models = new Dictionary<string, ModelConfig>
                {
                    ["original"] = new() { Id = "original" }
                }
            }
        };
        var config = await CreateConfigAsync(new SeeingAgentOptions(), providers);
        var registry = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance);
        registry.Register(new ProviderRegistration(new TestProvider("openai", [new() { Id = "original" }]), ProviderOrigin.Configured));
        registry.Register(new ProviderRegistration(new TestProvider("extension"), ProviderOrigin.Extension, OwnerId: "sample-extension"));
        using var catalog = new ModelConfigManager(
            config,
            registry,
            NullLogger<ModelConfigManager>.Instance);
        var replacement = new ModelConfig { Id = "replacement", Provider = "extension" };

        await catalog.UpdateModelAsync("openai/original", replacement, ct: TestContext.Current.CancellationToken);

        config.GetSection<Dictionary<string, ProviderConfig>>("Providers")["openai"].Models!["original"].Id.Should().Be("replacement");
        replacement.Provider.Should().Be("openai");
    }

    [Fact]
    public async Task Refresh_ProvidersAggregateConcurrently_AndFailuresAreIsolated()
    {
        var config = await CreateConfigAsync(new SeeingAgentOptions());
        var registry = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance);
        var gate = new ProviderCallGate(2);
        registry.Register(new ProviderRegistration(new GatedProvider("first", gate, [new() { Id = "visible" }]), ProviderOrigin.Extension, OwnerId: "gate"));
        registry.Register(new ProviderRegistration(new GatedProvider("second", gate, [new() { Id = "also-visible" }]), ProviderOrigin.Extension, OwnerId: "gate"));
        registry.Register(new ProviderRegistration(new TestProvider("failing", error: new InvalidOperationException("expected")), ProviderOrigin.Extension, OwnerId: "gate"));
        using var catalog = new ModelConfigManager(
            config,
            registry,
            NullLogger<ModelConfigManager>.Instance);

        await gate.AllStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        gate.Release.SetResult();
        await WaitUntilAsync(
            () => catalog.GetCatalogModels().Count == 2,
            TimeSpan.FromSeconds(5));

        catalog.GetCatalogModels().Keys.Should().BeEquivalentTo(
            "first/visible",
            "second/also-visible");
    }

    [Fact]
    public async Task Refresh_StaleResultDiscarded_LatestWins()
    {
        var config = await CreateConfigAsync(new SeeingAgentOptions());
        var registry = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance);
        var provider = new SequencedProvider();
        registry.Register(new ProviderRegistration(provider, ProviderOrigin.Extension, OwnerId: "sequence"));
        using var catalog = new ModelConfigManager(
            config,
            registry,
            NullLogger<ModelConfigManager>.Instance);

        await provider.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // 首个全量刷新仍挂起时再排一次全量刷新，使首个结果过期
        var latestRefresh = catalog.RefreshCatalogAsync(ct: TestContext.Current.CancellationToken);
        provider.ReleaseFirstCall.SetResult();
        await latestRefresh.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        catalog.GetCatalogModels().Keys.Should().ContainSingle().Which.Should().Be("sequence/latest");
    }

    [Fact]
    public async Task RefreshCatalogAsync_Full_WaitsUntilApplied_AndRecoversModels()
    {
        var config = await CreateConfigAsync(new SeeingAgentOptions());
        var registry = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance);
        var provider = new MutableModelsProvider("recoverable");
        provider.SetModels([]);
        registry.Register(new ProviderRegistration(provider, ProviderOrigin.Extension, OwnerId: "recover"));
        using var catalog = new ModelConfigManager(
            config,
            registry,
            NullLogger<ModelConfigManager>.Instance);

        await WaitUntilAsync(() => provider.CallCount >= 1, TimeSpan.FromSeconds(5));
        catalog.GetCatalogModels().Should().BeEmpty();

        provider.SetModels([new ModelConfig { Id = "recovered" }]);
        await catalog.RefreshCatalogAsync(ct: TestContext.Current.CancellationToken);

        catalog.GetCatalogModels().Keys.Should().ContainSingle().Which.Should().Be("recoverable/recovered");
    }

    [Fact]
    public async Task RefreshCatalogAsync_SingleProvider_UpdatesOnlyThatProvider()
    {
        var config = await CreateConfigAsync(new SeeingAgentOptions());
        var registry = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance);
        var first = new MutableModelsProvider("first");
        first.SetModels([new ModelConfig { Id = "a" }]);
        var second = new MutableModelsProvider("second");
        second.SetModels([new ModelConfig { Id = "b" }]);
        registry.Register(new ProviderRegistration(first, ProviderOrigin.Extension, OwnerId: "ext"));
        registry.Register(new ProviderRegistration(second, ProviderOrigin.Extension, OwnerId: "ext"));
        using var catalog = new ModelConfigManager(
            config,
            registry,
            NullLogger<ModelConfigManager>.Instance);

        await WaitUntilAsync(
            () => catalog.GetCatalogModels().Count == 2,
            TimeSpan.FromSeconds(5));

        var secondCallsBefore = second.CallCount;
        first.SetModels([new ModelConfig { Id = "a2" }]);
        second.SetModels([new ModelConfig { Id = "b2" }]);

        await catalog.RefreshCatalogAsync("first", TestContext.Current.CancellationToken);

        catalog.GetCatalogModels().Keys.Should().BeEquivalentTo("first/a2", "second/b");
        second.CallCount.Should().Be(secondCallsBefore);
    }

    [Fact]
    public async Task RefreshCatalogAsync_ConfiguredProvider_RereadsUserModels()
    {
        var providers = new Dictionary<string, ProviderConfig>
        {
            ["openai"] = new ProviderConfig
            {
                Id = "openai",
                Models = new Dictionary<string, ModelConfig>
                {
                    ["gpt"] = new() { Id = "gpt" }
                }
            }
        };
        var config = await CreateConfigAsync(new SeeingAgentOptions(), providers);
        var registry = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance);
        registry.Register(new ProviderRegistration(new TestProvider("openai", [new() { Id = "gpt" }]), ProviderOrigin.Configured));
        using var catalog = new ModelConfigManager(
            config,
            registry,
            NullLogger<ModelConfigManager>.Instance);

        await WaitUntilAsync(
            () => catalog.GetCatalogModels().ContainsKey("openai/gpt"),
            TimeSpan.FromSeconds(5));

        await config.SaveSectionAsync(
            "Providers",
            new Dictionary<string, ProviderConfig>
            {
                ["openai"] = new ProviderConfig
                {
                    Id = "openai",
                    Models = new Dictionary<string, ModelConfig>
                    {
                        ["gpt"] = new() { Id = "gpt" },
                        ["added"] = new() { Id = "added" }
                    }
                }
            },
            ConfigLevel.User, TestContext.Current.CancellationToken);

        await catalog.RefreshCatalogAsync("openai", TestContext.Current.CancellationToken);

        catalog.GetCatalogModels().Keys.Should().BeEquivalentTo("openai/gpt", "openai/added");
    }

    [Fact]
    public async Task ProvidersChanged_UnregisterOneProvider_DoesNotReloadRemainingProviders()
    {
        var config = await CreateConfigAsync(new SeeingAgentOptions());
        var registry = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance);
        var first = new MutableModelsProvider("first");
        first.SetModels([new ModelConfig { Id = "a" }]);
        var second = new MutableModelsProvider("second");
        second.SetModels([new ModelConfig { Id = "b" }]);
        registry.Register(new ProviderRegistration(first, ProviderOrigin.Extension, OwnerId: "ext"));
        registry.Register(new ProviderRegistration(second, ProviderOrigin.Extension, OwnerId: "ext"));
        using var catalog = new ModelConfigManager(
            config,
            registry,
            NullLogger<ModelConfigManager>.Instance);

        await WaitUntilAsync(() => catalog.GetCatalogModels().Count == 2, TimeSpan.FromSeconds(5));
        var firstCalls = first.CallCount;

        registry.Unregister("second");

        await WaitUntilAsync(
            () => catalog.GetCatalogModels().Keys.All(key => !key.StartsWith("second/", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5));

        first.CallCount.Should().Be(firstCalls);
    }

    [Fact]
    public async Task DeleteModelAsync_AlreadyDeleted_DoesNotPersistAgain()
    {
        var providers = new Dictionary<string, ProviderConfig>
        {
            ["openai"] = new ProviderConfig
            {
                Id = "openai",
                Models = new Dictionary<string, ModelConfig>
                {
                    ["gpt"] = new() { Id = "gpt" }
                }
            }
        };
        var config = await CreateConfigAsync(new SeeingAgentOptions(), providers);
        var registry = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance);
        registry.Register(new ProviderRegistration(new TestProvider("openai", [new() { Id = "gpt" }]), ProviderOrigin.Configured));
        using var catalog = new ModelConfigManager(
            config,
            registry,
            NullLogger<ModelConfigManager>.Instance);

        await WaitUntilAsync(
            () => catalog.GetCatalogModels().ContainsKey("openai/gpt"),
            TimeSpan.FromSeconds(5));

        var providersSaves = 0;
        config.ConfigChanged += (_, e) =>
        {
            if (e.ChangedSections.Contains("Providers", StringComparer.Ordinal))
                Interlocked.Increment(ref providersSaves);
        };

        await catalog.DeleteModelAsync("openai/gpt", ct: TestContext.Current.CancellationToken);
        await catalog.DeleteModelAsync("openai/gpt", ct: TestContext.Current.CancellationToken);

        providersSaves.Should().Be(1);
    }

    [Fact]
    public async Task ModelReloadHandler_ScopedProvidersChange_RefreshesOnlyThatProvider()
    {
        var config = await CreateConfigAsync(new SeeingAgentOptions());
        var registry = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance);
        var first = new MutableModelsProvider("first");
        first.SetModels([new ModelConfig { Id = "a" }]);
        var second = new MutableModelsProvider("second");
        second.SetModels([new ModelConfig { Id = "b" }]);
        registry.Register(new ProviderRegistration(first, ProviderOrigin.Extension, OwnerId: "ext"));
        registry.Register(new ProviderRegistration(second, ProviderOrigin.Extension, OwnerId: "ext"));
        using var catalog = new ModelConfigManager(
            config,
            registry,
            NullLogger<ModelConfigManager>.Instance);

        await WaitUntilAsync(() => catalog.GetCatalogModels().Count == 2, TimeSpan.FromSeconds(5));
        var firstCalls = first.CallCount;
        var secondCalls = second.CallCount;

        var handler = new ModelReloadHandler(catalog);
        await ((IReloadHandler)handler).ReloadAsync(
            new ConfigChange { ChangedSections = ["Providers"], ChangedKeys = ["first"] },
            TestContext.Current.CancellationToken);

        await WaitUntilAsync(() => first.CallCount > firstCalls, TimeSpan.FromSeconds(5));
        second.CallCount.Should().Be(secondCalls);
    }

    [Fact]
    public async Task ProvidersChanged_ProviderUnregistered_PrunesProviderRefreshVersion()
    {
        var config = await CreateConfigAsync(new SeeingAgentOptions());
        var registry = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance);
        using var catalog = new ModelConfigManager(
            config,
            registry,
            NullLogger<ModelConfigManager>.Instance);

        var provider = new MutableModelsProvider("ghost");
        provider.SetModels([new ModelConfig { Id = "m" }]);
        registry.Register(new ProviderRegistration(provider, ProviderOrigin.Extension, OwnerId: "ext"));

        await WaitUntilAsync(
            () => catalog.GetCatalogModels().ContainsKey("ghost/m"),
            TimeSpan.FromSeconds(5));
        HasProviderRefreshVersion(catalog, "ghost").Should().BeTrue();

        registry.Unregister("ghost");

        await WaitUntilAsync(
            () => catalog.GetCatalogModels().Keys.All(key => !key.StartsWith("ghost/", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5));

        HasProviderRefreshVersion(catalog, "ghost").Should().BeFalse();
    }

    [Fact]
    public async Task FullRefresh_PrunesRefreshVersionsOfInactiveProviders()
    {
        var config = await CreateConfigAsync(new SeeingAgentOptions());
        var registry = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance);
        using var catalog = new ModelConfigManager(
            config,
            registry,
            NullLogger<ModelConfigManager>.Instance);

        // 构造时会排队一次后台 "initial" 全量刷新；它完成时会裁剪「不在注册表中」的 phantom 条目，
        // 可能恰好落在 Set 与断言之间（并行跑全套件时更容易命中）。本用例只验证 FullRefresh 的裁剪语义，
        // 因此先排空一次刷新，并把写入与断言放进同一把缓存锁内，消除这个无关竞态。
        await catalog.RefreshCatalogAsync(ct: TestContext.Current.CancellationToken);

        lock (GetCacheLock(catalog))
        {
            SetProviderRefreshVersion(catalog, "phantom", 1L);
            HasProviderRefreshVersion(catalog, "phantom").Should().BeTrue();
        }

        await catalog.RefreshCatalogAsync(ct: TestContext.Current.CancellationToken);

        HasProviderRefreshVersion(catalog, "phantom").Should().BeFalse();
    }

    private static Dictionary<string, long> GetProviderRefreshVersions(ModelConfigManager catalog)
    {
        var field = typeof(ModelConfigManager).GetField(
            "_latestProviderRefreshVersions",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Dictionary<string, long>)field.GetValue(catalog)!;
    }

    private static object GetCacheLock(ModelConfigManager catalog)
    {
        var field = typeof(ModelConfigManager).GetField(
            "_cacheLock",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return field.GetValue(catalog)!;
    }

    private static bool HasProviderRefreshVersion(ModelConfigManager catalog, string providerId)
    {
        lock (GetCacheLock(catalog))
            return GetProviderRefreshVersions(catalog).ContainsKey(providerId);
    }

    private static void SetProviderRefreshVersion(ModelConfigManager catalog, string providerId, long version)
    {
        lock (GetCacheLock(catalog))
            GetProviderRefreshVersions(catalog)[providerId] = version;
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

    private async Task<UnifiedConfigManager> CreateConfigAsync(
        SeeingAgentOptions options,
        Dictionary<string, ProviderConfig>? providers = null)
    {
        var user = Path.Combine(_root, "user", ".seeing");
        var project = Path.Combine(_root, "project", ".seeing");
        Directory.CreateDirectory(user);
        Directory.CreateDirectory(project);
        await File.WriteAllTextAsync(
            Path.Combine(user, "seeing.json"),
            JsonSerializer.Serialize(new { SeeingAgent = options }));
        if (providers is { Count: > 0 })
        {
            await File.WriteAllTextAsync(
                Path.Combine(user, "providers.json"),
                JsonSerializer.Serialize(providers));
        }

        // 项目级可空：模型目录只读用户级

        var workspace = new Mock<IWorkspaceProvider>();
        workspace.SetupGet(item => item.UserSeeingDirectory).Returns(user);
        workspace.SetupGet(item => item.ProjectSeeingDirectory).Returns(project);
        var manager = new UnifiedConfigManager(workspace.Object, NullLogger<UnifiedConfigManager>.Instance, ConfigSectionRegistry.CreateWithSpine());
        await manager.LoadAsync();
        return manager;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    /// <summary>
    /// listModels 端到端守门（配置驱动 Provider 的远端目录合并）。
    /// <para>背景：`ModelConfigManager` 过去只对**扩展注册**的 Provider 调 `GetModelsAsync`，
    /// 配置驱动的（如 `Providers.typesafe`）只读静态 `Models` 表 —— 于是
    /// `ConfiguredLlmProvider.GetModelsAsync` 里的远端合并永不执行，jev 模型进不了目录。</para>
    /// <para>本用例锁住两件事：① 初次全量刷新时远端模型进目录；
    /// ② <c>RefreshCatalogAsync(providerId)</c>（模型页「刷新当前」与每次 CRUD 都走它）
    /// 之后远端模型**不消失**——后者曾因 `LoadProviderSliceAsync` 缺合并分支而丢失。</para>
    /// </summary>
    [Fact]
    public async Task RefreshCatalogAsync_ConfiguredProviderWithListModels_KeepsRemoteModels()
    {
        var providers = new Dictionary<string, ProviderConfig>
        {
            ["typesafe"] = new ProviderConfig
            {
                Id = "typesafe",
                Type = ProviderTypes.TypeSafe,
                Capabilities = ["decision"],
                ListModels = true,
                Models = new Dictionary<string, ModelConfig>
                {
                    ["static-one"] = new() { Id = "static-one", Types = [ModelType.Decision] }
                }
            }
        };
        var config = await CreateConfigAsync(new SeeingAgentOptions(), providers);

        var registry = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance);
        var provider = new CountingProvider(
            "typesafe",
            ProviderTypes.TypeSafe,
            LlmCapabilityKind.Decision,
            [new ModelConfig { Id = "jev-latest", Types = [ModelType.Decision] }]);
        registry.Register(new ProviderRegistration(provider, ProviderOrigin.Configured));

        using var catalog = new ModelConfigManager(
            config,
            registry,
            NullLogger<ModelConfigManager>.Instance);

        await WaitUntilAsync(
            () => catalog.GetCatalogModels().ContainsKey("typesafe/jev-latest"),
            TimeSpan.FromSeconds(5));

        catalog.GetCatalogModels().Keys.Should()
            .BeEquivalentTo("typesafe/static-one", "typesafe/jev-latest");

        // 单 Provider 刷新必须保留远端模型（曾在此丢失）
        await catalog.RefreshCatalogAsync("typesafe", TestContext.Current.CancellationToken);

        catalog.GetCatalogModels().Keys.Should().BeEquivalentTo(
            "typesafe/static-one", "typesafe/jev-latest");
    }

    /// <summary>
    /// 同一 id 既被扩展注册、又在 `Providers` 里声明 `listModels` 时，
    /// 每次全量刷新只能拉一次目录。
    /// <para>否则每次刷新对所有扩展 Provider 多发一次请求（DeepSeek/SiliconFlow/…），
    /// 未命中 TTL 时是 2× 网络，失败时日志也会翻倍。</para>
    /// </summary>
    [Fact]
    public async Task FullRefresh_ExtensionProviderAlsoInUserProviders_ShouldNotDoubleFetch()
    {
        var providers = new Dictionary<string, ProviderConfig>
        {
            ["dup"] = new ProviderConfig { Id = "dup", ListModels = true }
        };
        var config = await CreateConfigAsync(new SeeingAgentOptions(), providers);

        var registry = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance);
        var provider = new CountingProvider(
            "dup",
            ProviderTypes.OpenAi,
            LlmCapabilityKind.Chat,
            [new ModelConfig { Id = "m1" }]);
        // 扩展归属：走 extensionLoads 分支
        registry.Register(new ProviderRegistration(provider, ProviderOrigin.Extension, OwnerId: "ext.test"));

        using var catalog = new ModelConfigManager(
            config,
            registry,
            NullLogger<ModelConfigManager>.Instance);

        await WaitUntilAsync(() => provider.CallCount >= 1, TimeSpan.FromSeconds(5));
        var afterInitial = provider.CallCount;

        await catalog.RefreshCatalogAsync(null, TestContext.Current.CancellationToken);
        var afterSecond = provider.CallCount;

        (afterSecond - afterInitial).Should().Be(1,
            "扩展 Provider 由 extensionLoads 统一处理，不应再被 catalogLoads 拉一次");
    }

    /// <summary>记录 <see cref="GetModelsAsync"/> 调用次数的 Provider 替身。</summary>
    private sealed class CountingProvider(
        string id,
        string protocol,
        LlmCapabilityKind capability,
        IReadOnlyList<ModelConfig> models) : ILlmProvider
    {
        private int _callCount;

        public string Id { get; } = id;
        public string? Name => Id;
        public string Protocol { get; } = protocol;
        public LlmCapabilityKind Capabilities { get; } = capability;
        public int MaxRetries => 3;

        public int CallCount => Volatile.Read(ref _callCount);

        public ILlmCapabilityClient? GetClient(LlmCapabilityKind requested) => null;

        public Task<IReadOnlyList<ModelConfig>> GetModelsAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(models);
        }

        public Task<bool> TestConnectionAsync(
            string modelId,
            LlmCapabilityKind requested,
            CancellationToken cancellationToken)
            => Task.FromResult(true);
    }

    private sealed class TestProvider : ILlmProvider
    {
        private readonly IReadOnlyList<ModelConfig> _models;
        private readonly Exception? _error;

        public TestProvider(string id, IReadOnlyList<ModelConfig>? models = null, Exception? error = null)
        {
            Id = id;
            _models = models ?? [];
            _error = error;
        }

        public string Id { get; }
        public string? Name => Id;
        public string Protocol => ProviderTypes.OpenAi;
        public LlmCapabilityKind Capabilities => LlmCapabilityKind.Chat;
        public int MaxRetries => 3;
        public ILlmCapabilityClient? GetClient(LlmCapabilityKind capability) =>
            capability == LlmCapabilityKind.Chat ? Mock.Of<IChatCompletionClient>() : null;

        public Task<IReadOnlyList<ModelConfig>> GetModelsAsync(CancellationToken cancellationToken)
            => _error is null
                ? Task.FromResult(_models)
                : Task.FromException<IReadOnlyList<ModelConfig>>(_error);

        public Task<bool> TestConnectionAsync(
            string modelId,
            LlmCapabilityKind capability,
            CancellationToken cancellationToken)
            => Task.FromResult(true);
    }

    private sealed class MutableModelsProvider(string id) : ILlmProvider
    {
        private readonly object _lock = new();
        private IReadOnlyList<ModelConfig> _models = [];

        public string Id { get; } = id;
        public string? Name => Id;
        public string Protocol => ProviderTypes.OpenAi;
        public LlmCapabilityKind Capabilities => LlmCapabilityKind.Chat;
        public int MaxRetries => 3;
        public int CallCount { get; private set; }

        public void SetModels(IReadOnlyList<ModelConfig> models)
        {
            lock (_lock)
                _models = models;
        }

        public ILlmCapabilityClient? GetClient(LlmCapabilityKind capability) =>
            capability == LlmCapabilityKind.Chat ? Mock.Of<IChatCompletionClient>() : null;

        public Task<IReadOnlyList<ModelConfig>> GetModelsAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            lock (_lock)
                return Task.FromResult(_models);
        }

        public Task<bool> TestConnectionAsync(
            string modelId,
            LlmCapabilityKind capability,
            CancellationToken cancellationToken)
            => Task.FromResult(true);
    }

    private sealed class SequencedProvider : ILlmProvider
    {
        private int _calls;
        public string Id => "sequence";
        public string? Name => Id;
        public string Protocol => ProviderTypes.OpenAi;
        public LlmCapabilityKind Capabilities => LlmCapabilityKind.Chat;
        public int MaxRetries => 3;
        public TaskCompletionSource FirstCallStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstCall { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ILlmCapabilityClient? GetClient(LlmCapabilityKind capability) =>
            capability == LlmCapabilityKind.Chat ? Mock.Of<IChatCompletionClient>() : null;

        public async Task<IReadOnlyList<ModelConfig>> GetModelsAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                FirstCallStarted.TrySetResult();
                await ReleaseFirstCall.Task.WaitAsync(cancellationToken);
                return [new ModelConfig { Id = "stale" }];
            }

            return [new ModelConfig { Id = "latest" }];
        }

        public Task<bool> TestConnectionAsync(
            string modelId,
            LlmCapabilityKind capability,
            CancellationToken cancellationToken)
            => Task.FromResult(true);
    }

    private sealed class ProviderCallGate(int expectedCallCount)
    {
        private int _calls;

        public TaskCompletionSource AllStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task WaitAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == expectedCallCount)
                AllStarted.TrySetResult();

            await Release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class GatedProvider(
        string id,
        ProviderCallGate gate,
        IReadOnlyList<ModelConfig> models) : ILlmProvider
    {
        public string Id { get; } = id;
        public string? Name => Id;
        public string Protocol => ProviderTypes.OpenAi;
        public LlmCapabilityKind Capabilities => LlmCapabilityKind.Chat;
        public int MaxRetries => 3;

        public ILlmCapabilityClient? GetClient(LlmCapabilityKind capability) =>
            capability == LlmCapabilityKind.Chat ? Mock.Of<IChatCompletionClient>() : null;

        public async Task<IReadOnlyList<ModelConfig>> GetModelsAsync(CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken);
            return models;
        }

        public Task<bool> TestConnectionAsync(
            string modelId,
            LlmCapabilityKind capability,
            CancellationToken cancellationToken)
            => Task.FromResult(true);
    }
}
