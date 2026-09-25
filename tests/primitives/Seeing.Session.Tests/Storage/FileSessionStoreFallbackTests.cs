using FluentAssertions;
using Seeing.Session.Core;
using Seeing.Session.Storage;
using Xunit;

namespace Seeing.Session.Tests.Storage;

/// <summary>
/// FileSessionStore 原子替换失败路径：临时源保留与临时文件清理。
/// </summary>
public class FileSessionStoreFallbackTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "seeing-file-store-fallback-" + Guid.NewGuid().ToString("N"));

    public FileSessionStoreFallbackTests()
    {
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public async Task SaveAsync_WhenAllMovesFail_ShouldPreserveTempSource()
    {
        // 目标路径被目录占据，File.Move/File.Copy 均必失败（重试耗尽 + 备用策略失败）。
        // 失败时必须保留 .tmp 源文件（唯一可恢复副本）——不得在 catch 中删除 source。
        var store = new FileSessionStore(_dir);
        var data = SessionData.Create();
        data.Id = "ses_fail";
        var destPath = Path.Combine(_dir, data.Id + ".json");
        Directory.CreateDirectory(destPath);

        var act = async () => await store.SaveAsync(data, TestContext.Current.CancellationToken);

        // 重试耗尽后应走备用策略并统一包装为 IOException；失败时 source（.tmp）保留
        await act.Should().ThrowAsync<IOException>();
        var tempPath = destPath + ".tmp";
        File.Exists(tempPath).Should().BeTrue("失败时必须保留临时源文件供恢复");
        (await File.ReadAllTextAsync(tempPath, TestContext.Current.CancellationToken))
            .Should().Contain(data.Id);
    }

    [Fact]
    public async Task SaveAsync_WhenSuccessful_ShouldCleanUpTempFile()
    {
        var store = new FileSessionStore(_dir);
        var data = SessionData.Create();
        data.Id = "ses_ok";

        await store.SaveAsync(data, TestContext.Current.CancellationToken);

        var tempPath = Path.Combine(_dir, data.Id + ".json.tmp");
        File.Exists(tempPath).Should().BeFalse("成功后不得残留 .tmp 临时文件");
        (await store.LoadAsync(data.Id, TestContext.Current.CancellationToken)).Should().NotBeNull();
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, true);
    }
}
