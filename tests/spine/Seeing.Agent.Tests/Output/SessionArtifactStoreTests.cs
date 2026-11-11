using FluentAssertions;
using Seeing.Agent.Abstractions.Sessions;
using Seeing.Agent.Core.Output;
using Seeing.Agent.Paths;
using Seeing.Session.Storage;
using Xunit;

namespace Seeing.Agent.Tests.Output;

/// <summary>
/// 会话资源目录端口：工具产出的二进制必须落在**本会话 ref 目录**内，
/// 否则图片物化器会判越界并降级成文本占位（agent 就看不到截图了）。
/// </summary>
public class SessionArtifactStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _sessionId = "s_artifact";

    public SessionArtifactStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "seeing-artifacts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // 清理失败不影响断言
        }
    }

    private (ISessionArtifactStore Store, string RefDir) Create()
    {
        var outputStore = new SessionToolOutputStore(new FileSessionStore(_tempDir));
        return (new SessionArtifactStore(outputStore), outputStore.GetRefDirectory(_sessionId));
    }

    [Fact]
    public async Task Save_ShouldWriteInsideRefDirectory()
    {
        var (store, refDir) = Create();
        var bytes = new byte[] { 1, 2, 3, 4 };

        var path = await store.SaveAsync(_sessionId, "browser-shot", bytes, ".png");

        PathBoundary.IsWithin(path, refDir).Should().BeTrue("图片物化器只接受会话 ref 目录内的路径");
        File.Exists(path).Should().BeTrue();
        (await File.ReadAllBytesAsync(path)).Should().Equal(bytes);
        Path.GetExtension(path).Should().Be(".png");
    }

    [Fact]
    public async Task Save_SameNameTwice_ShouldProduceDistinctFilesWithOwnContent()
    {
        var (store, refDir) = Create();

        var first = await store.SaveAsync(_sessionId, "browser-shot", new byte[] { 1 }, ".png");
        var second = await store.SaveAsync(_sessionId, "browser-shot", new byte[] { 2 }, ".png");

        // 断言「内容各自独立」而不是「文件名不同」：文件名唯一是实现手段，内容不丢才是目的
        PathBoundary.IsWithin(first, refDir).Should().BeTrue();
        PathBoundary.IsWithin(second, refDir).Should().BeTrue();
        (await File.ReadAllBytesAsync(first)).Should().Equal(new byte[] { 1 });
        (await File.ReadAllBytesAsync(second)).Should().Equal(new byte[] { 2 });
    }

    [Fact]
    public async Task Save_ConcurrentSameName_ShouldAllKeepTheirOwnContent()
    {
        var (store, _) = Create();

        var paths = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            store.SaveAsync(_sessionId, "shot", new byte[] { (byte)i }, ".png")));

        // 目的：并发下不互相覆盖（每条内容都能读回自己的值），而不是「路径必须互不相同」
        for (var i = 0; i < paths.Length; i++)
        {
            File.Exists(paths[i]).Should().BeTrue();
            (await File.ReadAllBytesAsync(paths[i])).Should().Equal(new byte[] { (byte)i });
        }
    }

    [Fact]
    public async Task Save_PathTraversalExtension_ShouldStayInsideRefDirectory()
    {
        var (store, refDir) = Create();

        // 扩展名里塞路径分隔符：必须净化掉，文件不能跑到 ref 目录之外
        var path = await store.SaveAsync(_sessionId, "blob", new byte[] { 9 }, "../evil");

        PathBoundary.IsWithin(path, refDir).Should().BeTrue();
        Path.GetFileName(path).Should().NotContain("/").And.NotContain("\\");
    }

    [Fact]
    public async Task Save_InvalidExtension_ShouldFallBackToBin()
    {
        var (store, refDir) = Create();

        var path = await store.SaveAsync(_sessionId, "blob", new byte[] { 9 }, "??");

        PathBoundary.IsWithin(path, refDir).Should().BeTrue();
        Path.GetExtension(path).Should().Be(".bin");
    }

    [Fact]
    public async Task Save_MissingSessionId_ShouldThrow()
    {
        var (store, _) = Create();

        var act = () => store.SaveAsync("  ", "x", new byte[] { 1 }, ".png");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Save_MissingName_ShouldThrow()
    {
        var (store, _) = Create();

        var act = () => store.SaveAsync(_sessionId, "", new byte[] { 1 }, ".png");

        await act.Should().ThrowAsync<ArgumentException>();
    }
}
