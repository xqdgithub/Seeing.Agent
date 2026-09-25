using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Llm;
using Seeing.Agent.Llm;
using Xunit;

namespace Seeing.Agent.Tests.Llm;

/// <summary>
/// ProviderManager 并发加固回归：配置刷新与注册表变更事件并发触发时不得抛异常。
/// </summary>
public class ProviderManagerConcurrencyTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _tempDirectory =
        Path.Combine(Path.GetTempPath(), "provider-manager-concurrency-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RefreshAndRegistryChanged_Concurrent_NoException()
    {
        var providers = new Dictionary<string, ProviderConfig>
        {
            ["provider"] = PredefinedProviders.OpenAI("sk-test")
        };
        var configManager = await CreateConfigManagerAsync(providers);
        var registry = new ProviderRegistry(NullLogger<ProviderRegistry>.Instance);
        var factory = new Mock<ILlmClientFactory>();
        factory.Setup(candidate => candidate.SupportsType(It.IsAny<string>())).Returns(true);
        factory.Setup(candidate => candidate.Create(It.IsAny<ProviderConfig>()))
            .Returns(Mock.Of<ILlmClient>());
        using var sut = new ProviderManager(
            configManager,
            [factory.Object],
            Mock.Of<IModelConfigManager>(),
            registry,
            new Lazy<IModelCapabilityManager>(() => NullModelCapabilityManager.Instance),
            NullLogger<ProviderManager>.Instance);

        // 线程 A/B：反复保存同一 Provider（连接项变化触发重建），并发驱动 Refresh 修改内部字典。
        Task SaveLoop(int seed) => Task.Run(async () =>
        {
            for (var i = 0; i < 60; i++)
            {
                await sut.SaveProviderAsync(
                    "provider",
                    PredefinedProviders.OpenAI($"sk-{seed}-{i}"),
                    ct: TestContext.Current.CancellationToken);
            }
        });

        // 线程 C：反复触发注册表变更事件，驱动 OnProvidersChanged 遍历内部字典。
        var registryLoop = Task.Run(() =>
        {
            for (var i = 0; i < 400; i++)
            {
                var id = $"ext_{i}";
                registry.Register(new TestProvider(id), ownerExtensionId: id);
                registry.UnregisterByOwner(id);
            }
        });

        await Task.WhenAll(SaveLoop(1), SaveLoop(2), registryLoop);

        sut.GetProvider("provider").Should().NotBeNull();
    }

    private async Task<UnifiedConfigManager> CreateConfigManagerAsync(
        Dictionary<string, ProviderConfig> providers)
    {
        var userSeeingDirectory = Path.Combine(_tempDirectory, "user", ".seeing");
        var projectSeeingDirectory = Path.Combine(_tempDirectory, "project", ".seeing");
        Directory.CreateDirectory(userSeeingDirectory);
        Directory.CreateDirectory(projectSeeingDirectory);

        await File.WriteAllTextAsync(
            Path.Combine(projectSeeingDirectory, "seeing.json"),
            JsonSerializer.Serialize(new { SeeingAgent = new SeeingAgentOptions() }, JsonOptions));
        await File.WriteAllTextAsync(
            Path.Combine(userSeeingDirectory, "providers.json"),
            JsonSerializer.Serialize(providers, JsonOptions));

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

    private sealed class TestProvider(string id) : LlmProviderBase
    {
        public override string Id { get; } = id;

        public override string? Name => Id;

        public override ILlmClient GetClient() => Mock.Of<ILlmClient>();

        public override Task<IReadOnlyList<ModelConfig>> GetModelsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ModelConfig>>([]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
            Directory.Delete(_tempDirectory, recursive: true);
    }
}
