using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Provider.DeepSeek;
using Xunit;

namespace Seeing.Provider.DeepSeek.Tests;

public class DeepSeekConfigStoreTests
{
    [Fact]
    public async Task LoadAsync_MissingFile_ReturnsEmptyApiKey()
    {
        var dir = Path.Combine(Path.GetTempPath(), "seeing-deepseek-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new DeepSeekConfigStore(dir, NullLogger<DeepSeekConfigStore>.Instance);
            var options = await store.LoadAsync(TestContext.Current.CancellationToken);
            options.ApiKey.Should().BeNullOrEmpty();
            store.ConfigFilePath.Should().Be(Path.Combine(dir, "deepseek.json"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsApiKey()
    {
        var dir = Path.Combine(Path.GetTempPath(), "seeing-deepseek-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new DeepSeekConfigStore(dir, NullLogger<DeepSeekConfigStore>.Instance);
            await store.SaveAsync(new DeepSeekOptions { ApiKey = "sk-test" }, TestContext.Current.CancellationToken);
            File.Exists(store.ConfigFilePath).Should().BeTrue();
            var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);
            loaded.ApiKey.Should().Be("sk-test");
            Directory.GetFiles(dir).Should().ContainSingle(f =>
                f.EndsWith("deepseek.json", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_CorruptJson_ReturnsEmptyApiKey()
    {
        var dir = Path.Combine(Path.GetTempPath(), "seeing-deepseek-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "deepseek.json");
            await File.WriteAllTextAsync(path, "{ not-json", TestContext.Current.CancellationToken);
            var store = new DeepSeekConfigStore(dir, NullLogger<DeepSeekConfigStore>.Instance);
            var options = await store.LoadAsync(TestContext.Current.CancellationToken);
            options.ApiKey.Should().BeNullOrEmpty();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
