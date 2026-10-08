using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Provider.MiMo;
using Xunit;

namespace Seeing.Provider.MiMo.Tests;

public class MiMoConfigStoreTests
{
    [Fact]
    public async Task LoadAsync_MissingFile_ReturnsEmptyApiKey()
    {
        var dir = CreateTempDirectory();
        try
        {
            var store = new MiMoConfigStore(dir, NullLogger<MiMoConfigStore>.Instance);
            var options = await store.LoadAsync(TestContext.Current.CancellationToken);
            options.ApiKey.Should().BeNullOrEmpty();
            store.ConfigFilePath.Should().Be(Path.Combine(dir, "mimo.json"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsApiKey()
    {
        var dir = CreateTempDirectory();
        try
        {
            var store = new MiMoConfigStore(dir, NullLogger<MiMoConfigStore>.Instance);
            await store.SaveAsync(new MiMoOptions { ApiKey = "sk-test" }, TestContext.Current.CancellationToken);
            File.Exists(store.ConfigFilePath).Should().BeTrue();
            var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);
            loaded.ApiKey.Should().Be("sk-test");
            Directory.GetFiles(dir).Should().ContainSingle(f =>
                f.EndsWith("mimo.json", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_CorruptJson_ReturnsEmptyApiKey()
    {
        var dir = CreateTempDirectory();
        try
        {
            var path = Path.Combine(dir, "mimo.json");
            await File.WriteAllTextAsync(path, "{ not-json", TestContext.Current.CancellationToken);
            var store = new MiMoConfigStore(dir, NullLogger<MiMoConfigStore>.Instance);
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
            "seeing-mimo-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
