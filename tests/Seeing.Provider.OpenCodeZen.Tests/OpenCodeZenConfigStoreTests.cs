using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Provider.OpenCodeZen;
using Xunit;

namespace Seeing.Provider.OpenCodeZen.Tests;

public class OpenCodeZenConfigStoreTests
{
    [Fact]
    public async Task LoadAsync_MissingFile_ReturnsEmptyConfig()
    {
        var dir = CreateTempDirectory();
        try
        {
            var store = new OpenCodeZenConfigStore(dir, NullLogger<OpenCodeZenConfigStore>.Instance);
            var options = await store.LoadAsync(TestContext.Current.CancellationToken);
            options.ApiKey.Should().BeNullOrEmpty();
            options.ModelCapabilities.Should().BeNull();
            store.ConfigFilePath.Should().Be(Path.Combine(dir, "opencode-zen.json"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsApiKeyAndCapabilities()
    {
        var dir = CreateTempDirectory();
        try
        {
            var store = new OpenCodeZenConfigStore(dir, NullLogger<OpenCodeZenConfigStore>.Instance);
            await store.SaveAsync(
                new OpenCodeZenOptions
                {
                    ApiKey = "sk-test",
                    ModelCapabilities = new Dictionary<string, ModelCapabilityOverride>
                    {
                        ["future-free"] = new() { Context = 300_000, Output = 20_000 }
                    }
                },
                TestContext.Current.CancellationToken);

            File.Exists(store.ConfigFilePath).Should().BeTrue();
            var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);
            loaded.ApiKey.Should().Be("sk-test");
            loaded.ModelCapabilities.Should().ContainKey("future-free");
            loaded.ModelCapabilities!["future-free"].Context.Should().Be(300_000);
            loaded.ModelCapabilities["future-free"].Output.Should().Be(20_000);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_CorruptJson_ReturnsEmptyConfig()
    {
        var dir = CreateTempDirectory();
        try
        {
            var path = Path.Combine(dir, "opencode-zen.json");
            await File.WriteAllTextAsync(path, "{ not-json", TestContext.Current.CancellationToken);
            var store = new OpenCodeZenConfigStore(dir, NullLogger<OpenCodeZenConfigStore>.Instance);
            var options = await store.LoadAsync(TestContext.Current.CancellationToken);
            options.ApiKey.Should().BeNullOrEmpty();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var dir = Path.Combine(
            Path.GetTempPath(),
            "seeing-opencodezen-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
