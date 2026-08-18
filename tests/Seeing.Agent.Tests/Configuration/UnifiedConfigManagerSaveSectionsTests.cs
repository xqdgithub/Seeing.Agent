using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Configuration;
using Xunit;

namespace Seeing.Agent.Tests.Configuration;

public class UnifiedConfigManagerSaveSectionsTests
{
    [Fact]
    public async Task SaveSectionsAsync_Providers_ShouldReplaceRootWithoutSectionWrapper()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ucm-save-sections-" + Guid.NewGuid().ToString("N"));
        var userSeeing = Path.Combine(tempDir, ".seeing");
        Directory.CreateDirectory(userSeeing);

        var workspaceMock = new Mock<IWorkspaceProvider>();
        workspaceMock.Setup(w => w.WorkspaceRoot).Returns(tempDir);
        workspaceMock.Setup(w => w.UserSeeingDirectory).Returns(userSeeing);
        workspaceMock.Setup(w => w.ProjectSeeingDirectory).Returns(userSeeing);

        var configManager = new UnifiedConfigManager(
            workspaceMock.Object,
            NullLogger<UnifiedConfigManager>.Instance);

        var providers = new Dictionary<string, ProviderConfig>
        {
            ["openai"] = new()
            {
                Id = "openai",
                Type = ProviderType.OpenAI,
                BaseUrl = "https://api.openai.com/v1",
                ApiKey = "sk-test"
            }
        };

        await configManager.SaveSectionsAsync(ConfigLevel.User, new Dictionary<string, object>
        {
            ["Providers"] = providers
        });

        var providersPath = Path.Combine(userSeeing, "providers.json");
        File.Exists(providersPath).Should().BeTrue();

        var root = JsonNode.Parse(await File.ReadAllTextAsync(providersPath))!.AsObject();
        root.ContainsKey("Providers").Should().BeFalse();
        root.ContainsKey("openai").Should().BeTrue();
        root["openai"]!["baseURL"]!.GetValue<string>().Should().Be("https://api.openai.com/v1");

        var reloaded = configManager.GetSection<Dictionary<string, ProviderConfig>>("Providers");
        reloaded.Should().ContainKey("openai");
        reloaded["openai"].BaseUrl.Should().Be("https://api.openai.com/v1");

        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public async Task ConcurrentSaves_SameIndependentFile_ShouldNotThrowOrLeaveTempFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ucm-concurrent-" + Guid.NewGuid().ToString("N"));
        var userSeeing = Path.Combine(tempDir, ".seeing");
        Directory.CreateDirectory(userSeeing);

        var workspaceMock = new Mock<IWorkspaceProvider>();
        workspaceMock.Setup(w => w.WorkspaceRoot).Returns(tempDir);
        workspaceMock.Setup(w => w.UserSeeingDirectory).Returns(userSeeing);
        workspaceMock.Setup(w => w.ProjectSeeingDirectory).Returns(userSeeing);

        var configManager = new UnifiedConfigManager(
            workspaceMock.Object,
            NullLogger<UnifiedConfigManager>.Instance);

        var errors = new ConcurrentQueue<Exception>();
        var tasks = Enumerable.Range(0, 10).Select(i => Task.Run(async () =>
        {
            try
            {
                var providers = new Dictionary<string, ProviderConfig>
                {
                    [$"p{i}"] = new()
                    {
                        Id = $"p{i}",
                        BaseUrl = $"https://api{i}.example.com/v1"
                    }
                };
                await configManager.SaveSectionsAsync(ConfigLevel.User, new Dictionary<string, object>
                {
                    ["Providers"] = providers
                });
            }
            catch (Exception ex)
            {
                errors.Enqueue(ex);
            }
        })).ToArray();
        await Task.WhenAll(tasks);

        errors.Should().BeEmpty();

        var providersPath = Path.Combine(userSeeing, "providers.json");
        File.Exists(providersPath).Should().BeTrue();

        Directory.GetFiles(userSeeing, "*.tmp").Should().BeEmpty();

        var root = JsonNode.Parse(await File.ReadAllTextAsync(providersPath))!.AsObject();
        root.ContainsKey("Providers").Should().BeFalse();
        root.Count.Should().BeGreaterThan(0);
        root.Select(kv => kv.Value).Should().AllSatisfy(v => v!.GetValueKind().Should().Be(JsonValueKind.Object));

        Directory.Delete(tempDir, recursive: true);
    }
}
