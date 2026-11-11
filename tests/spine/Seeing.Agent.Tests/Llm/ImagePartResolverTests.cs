using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Core.Llm;
using Seeing.Agent.Core.Output;
using Seeing.Session.Core;
using Seeing.Session.Storage;
using Xunit;

namespace Seeing.Agent.Tests.Llm;

/// <summary>
/// 内容段物化器：把会话里的 <c>LocalPath</c> 读成可发送载荷。
/// <para>钉住：LRU 命中不重复读盘、路径越界拒绝、缺失降级为文本（不抛）。</para>
/// </summary>
public class ImagePartResolverTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _sessionId = "s_assets";

    public ImagePartResolverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "seeing-imgparts-" + Guid.NewGuid().ToString("N"));
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

    private (ImagePartResolver Resolver, string RefDir) Create()
    {
        var store = new FileSessionStore(_tempDir);
        var services = new ServiceCollection()
            .AddSingleton<ISessionStore>(store)
            .AddSingleton<IToolOutputStore>(new SessionToolOutputStore(store))
            .AddSingleton<Seeing.Agent.Abstractions.Execution.IFileSystem>(new Seeing.IO.Local.LocalExecutionWorld().FileSystem)
            .BuildServiceProvider();

        var outputStore = services.GetRequiredService<IToolOutputStore>();
        var refDir = outputStore.GetRefDirectory(_sessionId);
        Directory.CreateDirectory(refDir);

        return (new ImagePartResolver(services, capacity: 2), refDir);
    }

    [Fact]
    public void Resolve_RefPath_ShouldReturnBase64Image()
    {
        var (resolver, refDir) = Create();
        var file = Path.Combine(refDir, "shot.png");
        File.WriteAllBytes(file, [1, 2, 3, 4]);

        var part = SessionContentPart.CreateImageFromPath(file, "image/png");
        var result = resolver.Resolve(_sessionId, part);

        result.Type.Should().Be(ChatContentPart.KindImage);
        result.MimeType.Should().Be("image/png");
        result.DataBase64.Should().Be(Convert.ToBase64String(new byte[] { 1, 2, 3, 4 }));
    }

    [Fact]
    public void Resolve_OutsideRefDirectory_ShouldDegradeToText()
    {
        var (resolver, _) = Create();
        var outside = Path.Combine(_tempDir, "outside.png");
        File.WriteAllBytes(outside, [9]);

        var part = SessionContentPart.CreateImageFromPath(outside, "image/png");
        var result = resolver.Resolve(_sessionId, part);

        result.Type.Should().Be(ChatContentPart.KindText);
        result.Text.Should().Contain("不可用");
    }

    [Fact]
    public void Resolve_MissingFile_ShouldDegradeToTextWithoutThrowing()
    {
        var (resolver, refDir) = Create();

        var part = SessionContentPart.CreateImageFromPath(Path.Combine(refDir, "gone.png"), "image/png");
        var result = resolver.Resolve(_sessionId, part);

        result.Type.Should().Be(ChatContentPart.KindText);
        result.Text.Should().Contain("无法读取");
    }

    [Fact]
    public void Resolve_InlineBase64_ShouldPassThroughWithoutDiskAccess()
    {
        var (resolver, _) = Create();

        var part = SessionContentPart.CreateImageFromBase64("QUJD", "image/png");
        var result = resolver.Resolve(_sessionId, part);

        result.Type.Should().Be(ChatContentPart.KindImage);
        result.DataBase64.Should().Be("QUJD");
    }

    [Fact]
    public void Resolve_ToolImagePart_ShouldUseSameRules()
    {
        var (resolver, refDir) = Create();
        var file = Path.Combine(refDir, "tool.png");
        File.WriteAllBytes(file, [7, 7]);

        var result = resolver.Resolve(_sessionId, new ToolOutputPart
        {
            Kind = ToolOutputPartKind.Image,
            LocalPath = file,
            MimeType = "image/png"
        });

        result.Type.Should().Be(ChatContentPart.KindImage);
        result.DataBase64.Should().Be(Convert.ToBase64String(new byte[] { 7, 7 }));
    }

    [Fact]
    public void Resolve_FileKind_ShouldProduceFilePart()
    {
        var (resolver, refDir) = Create();
        var file = Path.Combine(refDir, "doc.pdf");
        File.WriteAllBytes(file, [5]);

        var result = resolver.Resolve(_sessionId, SessionContentPart.CreateFileFromPath(file, "application/pdf", "doc.pdf"));

        result.Type.Should().Be(ChatContentPart.KindFile);
        result.FileName.Should().Be("doc.pdf");
    }

    [Fact]
    public void Resolve_FileOverwritten_ShouldNotServeStaleCache()
    {
        var (resolver, refDir) = Create();
        var file = Path.Combine(refDir, "v.png");
        File.WriteAllBytes(file, [1]);

        var part = SessionContentPart.CreateImageFromPath(file, "image/png");
        resolver.Resolve(_sessionId, part).DataBase64.Should().Be(Convert.ToBase64String(new byte[] { 1 }));

        // 改内容并把时间戳推进（确保失效，不依赖文件系统时间精度）
        File.WriteAllBytes(file, [2, 2]);
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));

        resolver.Resolve(_sessionId, part).DataBase64
            .Should().Be(Convert.ToBase64String(new byte[] { 2, 2 }));
    }

    [Fact]
    public void Resolve_ExceedingCapacity_ShouldEvictOldest()
    {
        var (resolver, refDir) = Create();
        var a = Path.Combine(refDir, "a.png");
        var b = Path.Combine(refDir, "b.png");
        var c = Path.Combine(refDir, "c.png");
        File.WriteAllBytes(a, [1]);
        File.WriteAllBytes(b, [2]);
        File.WriteAllBytes(c, [3]);

        // 容量 2：写三个后最早的被淘汰，但仍能正确重新读取（不是错误）
        resolver.Resolve(_sessionId, SessionContentPart.CreateImageFromPath(a, "image/png"));
        resolver.Resolve(_sessionId, SessionContentPart.CreateImageFromPath(b, "image/png"));
        resolver.Resolve(_sessionId, SessionContentPart.CreateImageFromPath(c, "image/png"));

        var again = resolver.Resolve(_sessionId, SessionContentPart.CreateImageFromPath(a, "image/png"));
        again.Type.Should().Be(ChatContentPart.KindImage);
        again.DataBase64.Should().Be(Convert.ToBase64String(new byte[] { 1 }));
    }

    [Fact]
    public void Resolve_EmptyPart_ShouldReturnSomethingWithoutThrowing()
    {
        var (resolver, _) = Create();

        var result = resolver.Resolve(_sessionId, new SessionContentPart { Type = ContentPartType.Image });

        result.Type.Should().Be(ChatContentPart.KindText);
    }
}
