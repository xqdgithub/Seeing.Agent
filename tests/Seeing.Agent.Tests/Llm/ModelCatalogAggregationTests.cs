using Seeing.Agent.Abstractions.Configuration;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Configuration;
using Seeing.Agent.Llm;
using Seeing.Agent.Abstractions.Llm;
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
        registry.Register(
            new TestProvider("extension", [new() { Id = "initial-model" }]),
            ownerExtensionId: "sample-extension");
        using var catalog = new ModelConfigManager(
            config,
            registry,
            NullLogger<ModelConfigManager>.Instance);

        await WaitUntilAsync(
            () => catalog.GetModels().ContainsKey("extension/initial-model"),
            TimeSpan.FromSeconds(5));

        catalog.GetModel("extension/initial-model")!.Provider.Should().Be("extension");
    }

    [Fact]
    public async Task GetModels_ExtensionProvider_DynamicModelsVisibleAfterRefresh()
    {
        var config = await CreateConfigAsync(new SeeingAgentOptions
        {
            Providers =
            {
                ["openai"] = new ProviderConfig
                {
                    Id = "openai",
                    Models = new Dictionary<string, ModelConfig>
                    {
                        ["configured"] = new() { Id = "configured" }
                    }
                }
            }
        });
        var registry = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance);
        registry.Register(new TestProvider("openai", [new() { Id = "configured" }]));
        using var catalog = new ModelConfigManager(
            config,
            registry,
            NullLogger<ModelConfigManager>.Instance);
        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        catalog.ModelConfigChanged += (_, _) => refreshed.TrySetResult();

        registry.Register(
            new TestProvider("extension", [new() { Id = "dynamic-model" }]),
            ownerExtensionId: "sample-extension");

        await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        catalog.GetModels().Keys.Should().BeEquivalentTo(
            "openai/configured",
            "extension/dynamic-model");
        catalog.GetModel("extension/dynamic-model")!.Provider.Should().Be("extension");
    }

    [Fact]
    public async Task GetModels_OneProviderThrows_OthersStillVisible()
    {
        var config = await CreateConfigAsync(new SeeingAgentOptions());
        var registry = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance);
        registry.Register(new TestProvider("healthy", [new() { Id = "visible" }]), ownerExtensionId: "ext");
        registry.Register(new TestProvider("failing", error: new InvalidOperationException("expected")), ownerExtensionId: "ext");
        using var catalog = new ModelConfigManager(
            config,
            registry,
            NullLogger<ModelConfigManager>.Instance);
        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        catalog.ModelConfigChanged += (_, _) => refreshed.TrySetResult();

        registry.Register(new TestProvider("trigger", []), ownerExtensionId: "ext");

        await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        catalog.GetModels().Keys.Should().ContainSingle().Which.Should().Be("healthy/visible");
    }

    [Fact]
    public async Task AddModelAsync_ExtensionProvider_DoesNotPersistConfiguration()
    {
        var config = await CreateConfigAsync(new SeeingAgentOptions());
        var registry = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance);
        registry.Register(new TestProvider("extension"), ownerExtensionId: "sample-extension");
        using var catalog = new ModelConfigManager(
            config,
            registry,
            NullLogger<ModelConfigManager>.Instance);

        await catalog.AddModelAsync(
            "extension/dynamic",
            new ModelConfig { Id = "dynamic", Provider = "extension" });

        config.SeeingAgent.Providers.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveModelsAsync_ExtensionProvider_DoesNotPersistConfiguration()
    {
        var config = await CreateConfigAsync(new SeeingAgentOptions());
        var registry = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance);
        registry.Register(new TestProvider("extension"), ownerExtensionId: "sample-extension");
        using var catalog = new ModelConfigManager(
            config,
            registry,
            NullLogger<ModelConfigManager>.Instance);

        await catalog.SaveModelsAsync(
            "extension",
            new Dictionary<string, ModelConfig>
            {
                ["dynamic"] = new() { Id = "dynamic" }
            });

        config.SeeingAgent.Providers.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateModelAsync_ExistingCatalogOwnerWinsOverReplacementProvider()
    {
        var config = await CreateConfigAsync(new SeeingAgentOptions
        {
            Providers =
            {
                ["openai"] = new ProviderConfig
                {
                    Id = "openai",
                    Models = new Dictionary<string, ModelConfig>
                    {
                        ["original"] = new() { Id = "original" }
                    }
                }
            }
        });
        var registry = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance);
        registry.Register(new TestProvider("openai", [new() { Id = "original" }]));
        registry.Register(new TestProvider("extension"), ownerExtensionId: "sample-extension");
        using var catalog = new ModelConfigManager(
            config,
            registry,
            NullLogger<ModelConfigManager>.Instance);
        var replacement = new ModelConfig { Id = "replacement", Provider = "extension" };

        await catalog.UpdateModelAsync("openai/original", replacement);
        var saved = await config.GetSeeingAgentOptionsAtLevelAsync(
            ConfigLevel.User,
            TestContext.Current.CancellationToken);

        saved!.Providers["openai"].Models!["original"].Id.Should().Be("replacement");
        replacement.Provider.Should().Be("openai");
    }

    [Fact]
    public async Task Refresh_ProvidersAggregateConcurrently_AndFailuresAreIsolated()
    {
        var config = await CreateConfigAsync(new SeeingAgentOptions());
        var registry = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance);
        var gate = new ProviderCallGate(2);
        registry.Register(new GatedProvider("first", gate, [new() { Id = "visible" }]), ownerExtensionId: "gate");
        registry.Register(new GatedProvider("second", gate, [new() { Id = "also-visible" }]), ownerExtensionId: "gate");
        registry.Register(new TestProvider("failing", error: new InvalidOperationException("expected")), ownerExtensionId: "gate");
        using var catalog = new ModelConfigManager(
            config,
            registry,
            NullLogger<ModelConfigManager>.Instance);

        await gate.AllStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        gate.Release.SetResult();
        await WaitUntilAsync(
            () => catalog.GetModels().Count == 2,
            TimeSpan.FromSeconds(5));

        catalog.GetModels().Keys.Should().BeEquivalentTo(
            "first/visible",
            "second/also-visible");
    }

    [Fact]
    public async Task Refresh_StaleResultDiscarded_LatestWins()
    {
        var config = await CreateConfigAsync(new SeeingAgentOptions());
        var registry = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance);
        var provider = new SequencedProvider();
        registry.Register(provider, ownerExtensionId: "sequence");
        using var catalog = new ModelConfigManager(
            config,
            registry,
            NullLogger<ModelConfigManager>.Instance);
        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        catalog.ModelConfigChanged += (_, _) => refreshed.TrySetResult();

        registry.Register(new TestProvider("trigger"), ownerExtensionId: "sequence");
        await provider.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        registry.Register(new TestProvider("trigger"), ownerExtensionId: "sequence");
        provider.ReleaseFirstCall.SetResult();

        await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        catalog.GetModels().Keys.Should().ContainSingle().Which.Should().Be("sequence/latest");
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

    private async Task<UnifiedConfigManager> CreateConfigAsync(SeeingAgentOptions options)
    {
        var user = Path.Combine(_root, "user", ".seeing");
        var project = Path.Combine(_root, "project", ".seeing");
        Directory.CreateDirectory(user);
        Directory.CreateDirectory(project);
        await File.WriteAllTextAsync(
            Path.Combine(user, "seeing.json"),
            JsonSerializer.Serialize(new { SeeingAgent = options }));
        // 项目级可空：模型目录只读用户级

        var workspace = new Mock<IWorkspaceProvider>();
        workspace.SetupGet(item => item.UserSeeingDirectory).Returns(user);
        workspace.SetupGet(item => item.ProjectSeeingDirectory).Returns(project);
        var manager = new UnifiedConfigManager(workspace.Object, NullLogger<UnifiedConfigManager>.Instance);
        await manager.LoadAsync();
        return manager;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
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
        public int MaxRetries => 3;
        public ILlmClient GetClient() => throw new NotSupportedException();

        public Task<IReadOnlyList<ModelConfig>> GetModelsAsync(CancellationToken cancellationToken)
            => _error is null
                ? Task.FromResult(_models)
                : Task.FromException<IReadOnlyList<ModelConfig>>(_error);

        public Task<bool> TestConnectionAsync(string modelId, CancellationToken cancellationToken)
            => Task.FromResult(true);
    }

    private sealed class SequencedProvider : ILlmProvider
    {
        private int _calls;
        public string Id => "sequence";
        public string? Name => Id;
        public int MaxRetries => 3;
        public TaskCompletionSource FirstCallStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstCall { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ILlmClient GetClient() => throw new NotSupportedException();

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

        public Task<bool> TestConnectionAsync(string modelId, CancellationToken cancellationToken)
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
        public int MaxRetries => 3;

        public ILlmClient GetClient() => throw new NotSupportedException();

        public async Task<IReadOnlyList<ModelConfig>> GetModelsAsync(CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken);
            return models;
        }

        public Task<bool> TestConnectionAsync(string modelId, CancellationToken cancellationToken)
            => Task.FromResult(true);
    }
}
