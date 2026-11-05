using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Seeing.Provider.TypeSafeApi.Tests;

/// <summary>
/// 配置文件存储：新路径写入 + 旧 <c>systemone.json</c> 只读回退。
/// </summary>
public class TypeSafeApiConfigStoreTests
{
    [Fact]
    public void ConfigFilePath_ShouldUseNewFileName()
    {
        var root = NewRoot();
        var store = new TypeSafeApiConfigStore(root, NullLogger<TypeSafeApiConfigStore>.Instance);

        store.ConfigFilePath.Should().EndWith(Path.Combine("providers", "typesafe-api.json"));
    }

    [Fact]
    public async Task LoadAsync_WithoutAnyFile_ShouldReturnDefaults()
    {
        var store = new TypeSafeApiConfigStore(NewRoot(), NullLogger<TypeSafeApiConfigStore>.Instance);

        var options = await store.LoadAsync(TestContext.Current.CancellationToken);

        options.ApiKey.Should().BeNull();
        options.Models.Should().ContainKey(TypeSafeApiOptions.SeedModelId);
    }

    [Fact]
    public async Task SaveAsync_ThenLoad_ShouldRoundTrip()
    {
        var root = NewRoot();
        var store = new TypeSafeApiConfigStore(root, NullLogger<TypeSafeApiConfigStore>.Instance);

        await store.SaveAsync(
            new TypeSafeApiOptions { ApiKey = "sk-new" },
            TestContext.Current.CancellationToken);

        var options = await store.LoadAsync(TestContext.Current.CancellationToken);

        options.ApiKey.Should().Be("sk-new");
        options.Models.Should().ContainKey(TypeSafeApiOptions.SeedModelId);
    }

    /// <summary>
    /// 旧内建配置里的 apiKey 不得静默丢失：新文件不存在时回退读
    /// <c>providers/systemone.json</c>（只读，保存一律写新文件）。
    /// </summary>
    [Fact]
    public async Task LoadAsync_WhenOnlyLegacyFileExists_ShouldReadIt()
    {
        var root = NewRoot();
        var store = new TypeSafeApiConfigStore(root, NullLogger<TypeSafeApiConfigStore>.Instance);

        Directory.CreateDirectory(Path.GetDirectoryName(store.LegacyConfigFilePath)!);
        await File.WriteAllTextAsync(
            store.LegacyConfigFilePath,
            """{ "apiKey": "sk-legacy", "name": "Legacy" }""",
            TestContext.Current.CancellationToken);

        var options = await store.LoadAsync(TestContext.Current.CancellationToken);

        // 旧文件里的其它字段（如已废弃的 name/baseURL）被忽略也不报错——只关心凭据不丢。
        options.ApiKey.Should().Be("sk-legacy", "旧文件里的凭据不能因改名而静默清空");
    }

    /// <summary>新文件已存在时不得再读旧文件（否则迁移后仍被旧值覆盖）。</summary>
    [Fact]
    public async Task LoadAsync_WhenNewFileExists_ShouldPreferItOverLegacy()
    {
        var root = NewRoot();
        var store = new TypeSafeApiConfigStore(root, NullLogger<TypeSafeApiConfigStore>.Instance);

        Directory.CreateDirectory(Path.GetDirectoryName(store.LegacyConfigFilePath)!);
        await File.WriteAllTextAsync(
            store.LegacyConfigFilePath,
            """{ "apiKey": "sk-legacy" }""",
            TestContext.Current.CancellationToken);

        await store.SaveAsync(
            new TypeSafeApiOptions { ApiKey = "sk-new" },
            TestContext.Current.CancellationToken);

        var options = await store.LoadAsync(TestContext.Current.CancellationToken);

        options.ApiKey.Should().Be("sk-new");
    }

    private static string NewRoot()
        => Path.Combine(Path.GetTempPath(), "typesafe-store-" + Guid.NewGuid().ToString("N"));
}
