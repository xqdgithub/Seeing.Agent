using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Scenarios;
using Seeing.Agent.Core.CapabilitySets;
using Seeing.Agent.Hosting.Execution;
using Seeing.Agent.Core.Llm;
using Seeing.Agent.Llm;
using Seeing.Agent.Core.Modules;
using Seeing.Session.Core;
using Seeing.Session.Storage;
using Xunit;

namespace Seeing.Agent.Tests.Invariants;

/// <summary>
/// P9-T15：存量迁移 + Scenario 纯字符串不变量（§8）。
/// </summary>
public class MigrationTests : IDisposable
{
    private readonly string _tempDirectory =
        Path.Combine(Path.GetTempPath(), "p9-t15-migrate-" + Guid.NewGuid().ToString("N"));

    private static readonly JsonSerializerOptions s_sessionJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public async Task LegacyProvidersJson_PascalCaseType_NormalizesWithoutError_FirstSaveWritesLowercase()
    {
        var userSeeing = Path.Combine(_tempDirectory, "user", ".seeing");
        Directory.CreateDirectory(userSeeing);

        await File.WriteAllTextAsync(
            Path.Combine(userSeeing, "providers.json"),
            """
            {
              "legacy-openai": {
                "id": "legacy-openai",
                "type": "OpenAI",
                "baseURL": "https://api.example.com/v1",
                "apiKey": "sk-legacy"
              },
              "legacy-anthropic": {
                "id": "legacy-anthropic",
                "type": "Anthropic",
                "baseURL": "https://api.anthropic.example/v1",
                "apiKey": "sk-anth"
              }
            }
            """, TestContext.Current.CancellationToken);

        var configManager = await CreateConfigManagerAsync(userSeeing);

        var providers = configManager.GetSection<Dictionary<string, ProviderConfig>>("Providers");
        providers["legacy-openai"].Type.Should().Be(ProviderTypes.OpenAi);
        providers["legacy-anthropic"].Type.Should().Be(ProviderTypes.Anthropic);

        var factory = new Mock<ILlmClientFactory>();
        factory.Setup(f => f.SupportsType(It.IsAny<string>()))
            .Returns((string t) => string.Equals(t, ProviderTypes.OpenAi, StringComparison.OrdinalIgnoreCase));
        factory.Setup(f => f.Create(It.IsAny<ProviderConfig>()))
            .Returns(Mock.Of<ILlmClient>());

        using var sut = new ProviderManager(
            configManager,
            [factory.Object],
            Mock.Of<IModelConfigManager>(),
            new ProviderRegistry(NullLogger<ProviderRegistry>.Instance),
            new Lazy<IModelCapabilityManager>(() => NullModelCapabilityManager.Instance),
            NullLogger<ProviderManager>.Instance);

        var loaded = providers["legacy-openai"];
        await sut.SaveProviderAsync("legacy-openai", loaded, ConfigLevel.User, TestContext.Current.CancellationToken);

        var root = JsonNode.Parse(
            await File.ReadAllTextAsync(Path.Combine(userSeeing, "providers.json"), TestContext.Current.CancellationToken))!.AsObject();
        root["legacy-openai"]!["type"]!.GetValue<string>().Should().Be("openai");
        root["legacy-openai"]!["type"]!.GetValue<string>().Should().NotBe("OpenAI");
    }

    [Fact]
    public async Task SessionData_MissingScenario_DeserializesNull_FallsBackToProcess_DoesNotForceWrite()
    {
        var storeDir = Path.Combine(_tempDirectory, "sessions");
        Directory.CreateDirectory(storeDir);

        // 存量会话 JSON：无 scenario 字段
        var legacyPath = Path.Combine(storeDir, "ses_legacy.json");
        await File.WriteAllTextAsync(
            legacyPath,
            """
            {
              "id": "ses_legacy",
              "title": "Old",
              "partitionId": "default",
              "messages": []
            }
            """, TestContext.Current.CancellationToken);

        var store = new FileSessionStore(storeDir, NullLogger<FileSessionStore>.Instance);
        var loaded = await store.LoadAsync("ses_legacy", TestContext.Current.CancellationToken);

        loaded.Should().NotBeNull();
        loaded!.Scenario.Should().BeNull();
        loaded.ResolveScenario("code").Should().Be("code");

        // 结算回退进程级，不报错
        var catalog = new ModuleCatalog();
        catalog.ReplaceAvailable(
        [
            new ModuleDescriptor("git", ["git_status"], [], []),
            new ModuleDescriptor("memory", ["memory_search"], [], []),
            new ModuleDescriptor("filesystem", ["read"], [], []),
            new ModuleDescriptor("basic", ["current_time"], [], [])
        ]);
        catalog.ReplaceEnabled(["git", "memory", "filesystem", "basic"]);

        var settle = SessionSettlement.Compute(
            loaded, catalog, processScenario: "work", userToolsDisabled: null);
        settle.ScenarioName.Should().Be("work");
        settle.SettledToolIds.Should().Contain("memory_search");
        settle.SettledToolIds.Should().NotContain("git_status");

        // 保存后仍不强制写入非 null Scenario（保持 null / 不发明进程级值）
        loaded.Scenario.Should().BeNull();
        await store.SaveAsync(loaded, TestContext.Current.CancellationToken);

        var roundTripJson = await File.ReadAllTextAsync(legacyPath, TestContext.Current.CancellationToken);
        var roundTrip = JsonSerializer.Deserialize<SessionData>(roundTripJson, s_sessionJson);
        roundTrip!.Scenario.Should().BeNull();
        // 不得被强制写成进程级 "work"
        roundTripJson.Should().NotContain("\"scenario\": \"work\"");
    }

    [Fact]
    public async Task BuiltInScenarios_ArePureStringIds_CoreDoesNotReferenceCapabilityPackages_UnknownIdsWarnAndIgnore()
    {
        foreach (var scenario in BuiltInScenarios.All)
        {
            scenario.Modules.Should().OnlyContain(id => !string.IsNullOrWhiteSpace(id));
            scenario.Modules.Should().OnlyContain(id => id.GetType() == typeof(string));
        }

        BuiltInScenarios.Code.Modules.Should().Contain("git");
        BuiltInScenarios.Work.Modules.Should().Contain("memory");

        var core = typeof(BuiltInScenarios).Assembly;
        var forbidden = new HashSet<string>(StringComparer.Ordinal)
        {
            "Seeing.Agent.Tools.Git",
            "Seeing.Agent.Memory",
            "Seeing.Agent.Scheduler",
        };
        var refs = core.GetReferencedAssemblies().Select(a => a.Name).Where(n => n != null).Cast<string>();
        refs.Should().NotContain(n => forbidden.Contains(n),
            "Core 内置 Scenario 只用字符串 id，不得编译引用能力包");

        // 未引用 id 结算：告警忽略、不拒启
        var engine = new SettlementEngine(new ModuleCatalog(), NullLogger<SettlementEngine>.Instance);
        var result = await engine.SettleAsync(new SettlementInput
        {
            Available =
            [
                new ModuleDescriptor("io.local", [], [], []),
                new ModuleDescriptor("basic", [], [], []),
            ],
            ConfiguredBoot = "minimal",
            ResolveCapabilitySet = _ => new CapabilitySetDefinition(
                "minimal",
                ["io.local", "basic", "never-referenced-capability"],
                Array.Empty<string>()),
        }, TestContext.Current.CancellationToken);

        result.Enabled.Should().BeEquivalentTo(["basic", "io.local"]);
        result.Warnings.Should().Contain(w =>
            w.Contains("never-referenced-capability", StringComparison.Ordinal));
    }

    private async Task<UnifiedConfigManager> CreateConfigManagerAsync(string userSeeing)
    {
        var projectSeeing = Path.Combine(_tempDirectory, "project", ".seeing");
        Directory.CreateDirectory(projectSeeing);
        await File.WriteAllTextAsync(
            Path.Combine(userSeeing, "seeing.json"),
            """{"SeeingAgent":{}}""");

        var workspace = new Mock<IWorkspaceProvider>();
        workspace.Setup(w => w.UserSeeingDirectory).Returns(userSeeing);
        workspace.Setup(w => w.ProjectSeeingDirectory).Returns(projectSeeing);

        var manager = new UnifiedConfigManager(
            workspace.Object,
            NullLogger<UnifiedConfigManager>.Instance,
            ConfigSectionRegistry.CreateWithSpine());
        await manager.LoadAsync();
        return manager;
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
            Directory.Delete(_tempDirectory, recursive: true);
    }
}
