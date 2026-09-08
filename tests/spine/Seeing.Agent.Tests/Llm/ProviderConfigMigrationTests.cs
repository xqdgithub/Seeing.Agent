using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Llm;
using Seeing.Agent.Llm;
using Xunit;

namespace Seeing.Agent.Tests.Llm;

/// <summary>
/// 存量 providers.json 中 PascalCase Type（OpenAI/Anthropic）→ 小写迁移。
/// </summary>
public class ProviderConfigMigrationTests : IDisposable
{
    private readonly string _tempDirectory =
        Path.Combine(Path.GetTempPath(), "provider-type-migrate-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("OpenAI", "openai")]
    [InlineData("OPENAI", "openai")]
    [InlineData("openai", "openai")]
    [InlineData("Anthropic", "anthropic")]
    [InlineData("ANTHROPIC", "anthropic")]
    [InlineData("anthropic", "anthropic")]
    public void ProviderTypes_Normalize_MapsKnownAliases(string input, string expected)
        => ProviderTypes.Normalize(input).Should().Be(expected);

    [Fact]
    public void ProviderTypes_Normalize_UnknownPreserved()
        => ProviderTypes.Normalize("CustomVendor").Should().Be("CustomVendor");

    [Fact]
    public void ProviderConfig_TypeSetter_NormalizesPascalCase()
    {
        var config = new ProviderConfig { Type = "OpenAI" };
        config.Type.Should().Be(ProviderTypes.OpenAi);

        config.Type = "Anthropic";
        config.Type.Should().Be(ProviderTypes.Anthropic);
    }

    [Fact]
    public async Task Load_LegacyProvidersJson_PascalCaseType_NormalizesWithoutError()
    {
        var userSeeing = Path.Combine(_tempDirectory, "user", ".seeing");
        Directory.CreateDirectory(userSeeing);

        // 存量文件：PascalCase Type，不经 ProviderConfig 序列化写出
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
            """);

        var configManager = await CreateConfigManagerAsync(userSeeing);

        var providers = configManager.GetSection<Dictionary<string, ProviderConfig>>("Providers");
        providers.Should().ContainKey("legacy-openai");
        providers["legacy-openai"].Type.Should().Be(ProviderTypes.OpenAi);
        providers["legacy-anthropic"].Type.Should().Be(ProviderTypes.Anthropic);
    }

    [Fact]
    public async Task FirstSave_AfterLoadingLegacy_WritesLowercaseType()
    {
        var userSeeing = Path.Combine(_tempDirectory, "user", ".seeing");
        Directory.CreateDirectory(userSeeing);

        await File.WriteAllTextAsync(
            Path.Combine(userSeeing, "providers.json"),
            """
            {
              "legacy": {
                "id": "legacy",
                "type": "OpenAI",
                "name": "Legacy OpenAI",
                "baseURL": "https://api.example.com/v1",
                "apiKey": "sk-legacy"
              }
            }
            """);

        var configManager = await CreateConfigManagerAsync(userSeeing);
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
            NullLogger<ProviderManager>.Instance);

        var loaded = configManager.GetSection<Dictionary<string, ProviderConfig>>("Providers")["legacy"];
        loaded.Type.Should().Be(ProviderTypes.OpenAi);

        await sut.SaveProviderAsync("legacy", loaded, ConfigLevel.User, TestContext.Current.CancellationToken);

        var providersPath = Path.Combine(userSeeing, "providers.json");
        var root = JsonNode.Parse(await File.ReadAllTextAsync(providersPath))!.AsObject();
        root["legacy"]!["type"]!.GetValue<string>().Should().Be("openai");
        root["legacy"]!["type"]!.GetValue<string>().Should().NotBe("OpenAI");
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
