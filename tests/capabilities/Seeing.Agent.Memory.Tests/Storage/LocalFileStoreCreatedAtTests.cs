using FluentAssertions;
using Seeing.Agent.Memory.Core.Storage;
using Xunit;

namespace Seeing.Agent.Memory.Tests.Storage;

/// <summary>
/// created_at 键名解析。写入点统一写 snake_case（created_at），
/// 历史上解析器只读 camelCase（createdAt），导致每个文件的 CreatedAt 都回落 UtcNow。
/// 本组用例是那次错配的回归锚点。
/// </summary>
public class LocalFileStoreCreatedAtTests : IDisposable
{
    private readonly string _dir;
    private readonly LocalFileStore _store;

    public LocalFileStoreCreatedAtTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "Seeing.Agent.Memory.Tests", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_dir, "daily"));
        _store = new LocalFileStore(_dir);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string WriteFile(string relPath, string content)
    {
        var full = Path.Combine(_dir, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    [Fact]
    public async Task ReadAsync_With_SnakeCase_CreatedAt_Should_Use_Written_Value()
    {
        // 回归锚点：修之前这条必红（读出来是 UtcNow）。
        WriteFile("daily/2026-01-02/a.md", """
            ---
            id: a
            type: daily
            title: "旧记忆"
            created_at: 2026-01-02T03:04:05+00:00
            ---

            正文
            """);

        var node = await _store.ReadAsync("daily/2026-01-02/a.md", TestContext.Current.CancellationToken);

        node!.Metadata.CreatedAt.Should().Be(
            DateTimeOffset.Parse("2026-01-02T03:04:05+00:00"),
            "写入点写的是 created_at，解析器必须读它");
    }

    [Fact]
    public async Task ReadAsync_With_CamelCase_CreatedAt_Should_Still_Work()
    {
        WriteFile("daily/2026-01-02/b.md", """
            ---
            id: b
            type: daily
            createdAt: 2026-02-03T04:05:06+00:00
            ---

            正文
            """);

        var node = await _store.ReadAsync("daily/2026-01-02/b.md", TestContext.Current.CancellationToken);

        node!.Metadata.CreatedAt.Should().Be(DateTimeOffset.Parse("2026-02-03T04:05:06+00:00"));
    }

    [Fact]
    public async Task ReadAsync_Without_CreatedAt_Should_FallBack_To_FileMtime_Not_UtcNow()
    {
        // session/{id}/index.md 由 AppendSessionIndexAsync 手写头，只有 type + title。
        // 回落 UtcNow 会让它每次都算「今天」，时间分布被永久污染。
        var full = WriteFile("session/s1/index.md", """
            ---
            type: session
            title: session-index
            ---

            - 12:00:00 [fact] 某事 → [[daily/2026-01-02/x.md]]
            """);

        // 必须显式把 mtime 拨到过去：刚写完的文件 mtime 约等于「现在」，
        // 无法与 UtcNow 回落区分，那样的断言是无效的。
        var oldMtime = new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(full, oldMtime);

        var node = await _store.ReadAsync("session/s1/index.md", TestContext.Current.CancellationToken);

        node!.Metadata.CreatedAt.Should().Be(new DateTimeOffset(oldMtime, TimeSpan.Zero),
            "缺 created_at 时回落文件 mtime");
        node.Metadata.CreatedAt.Year.Should().Be(2025,
            "回落 UtcNow 会让这个文件永远算作「今天」，时间分布被永久污染");
    }

    [Fact]
    public async Task ReadAsync_Should_Parse_SourceSession_From_FrontMatter()
    {
        WriteFile("daily/2026-01-02/c.md", """
            ---
            id: c
            type: daily
            source_session: sess-abc123
            created_at: 2026-01-02T03:04:05+00:00
            ---

            正文
            """);

        var node = await _store.ReadAsync("daily/2026-01-02/c.md", TestContext.Current.CancellationToken);

        node!.Metadata.SourceSession.Should().Be("sess-abc123");
    }

    [Fact]
    public async Task ReadAsync_Should_Infer_SourceSession_From_SessionIndexPath()
    {
        WriteFile("session/sess-xyz789/index.md", """
            ---
            type: session
            title: session-index
            ---

            正文
            """);

        var node = await _store.ReadAsync("session/sess-xyz789/index.md", TestContext.Current.CancellationToken);

        node!.Metadata.SourceSession.Should().Be("sess-xyz789",
            "session/{id}/index.md 的 id 就是来源会话，路径约定应属存储层");
    }
}
