using FluentAssertions;
using Seeing.Session.Core;
using Seeing.Session.Storage;
using Xunit;

namespace Seeing.Session.Tests.Serialization;

/// <summary>
/// 会话落盘可读性：中文标题必须以原文写入文件，且旧格式（\uXXXX）仍能被读回。
/// </summary>
public sealed class SessionStoreReadableJsonTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "seeing-readable-json", Guid.NewGuid().ToString("N"));

    public SessionStoreReadableJsonTests()
    {
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public async Task SaveAsync_WithChineseTitle_ShouldWriteReadableText()
    {
        var store = new FileSessionStore(_dir);
        var data = SessionData.Create();
        data.Id = "ses_readable";
        data.Title = "新会话：中文标题";

        await store.SaveAsync(data, TestContext.Current.CancellationToken);

        var text = await File.ReadAllTextAsync(
            Path.Combine(_dir, "ses_readable.json"), TestContext.Current.CancellationToken);

        text.Should().Contain("新会话：中文标题");
        text.Should().NotContain("\\u");
    }

    [Fact]
    public async Task LoadAsync_WithLegacyEscapedFile_ShouldSucceed()
    {
        // 兼容性护栏：历史文件里是 \uXXXX 形式，改造后读回不能出错。
        var legacyPath = Path.Combine(_dir, "ses_legacy.json");
        await File.WriteAllTextAsync(
            legacyPath,
            """
            {
              "id": "ses_legacy",
              "title": "\u65B0\u4F1A\u8BDD",
              "partitionId": "default"
            }
            """,
            TestContext.Current.CancellationToken);

        var store = new FileSessionStore(_dir);
        var loaded = await store.LoadAsync("ses_legacy", TestContext.Current.CancellationToken);

        loaded.Should().NotBeNull();
        loaded!.Title.Should().Be("新会话");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
