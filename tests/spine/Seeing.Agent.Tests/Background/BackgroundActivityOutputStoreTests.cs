using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Background;
using Seeing.IO.Local;
using Xunit;

namespace Seeing.Agent.Tests.Background;

/// <summary>
/// <see cref="BackgroundActivityOutputStore"/> 的追加写、限额截断、分页读与路径校验。
/// <para>
/// 断言全部走可观察结果（文件字节、分页游标、状态标记），不使用固定时长等待。
/// </para>
/// </summary>
public class BackgroundActivityOutputStoreTests : IDisposable
{
    private const long DefaultLimitBytes = 5 * 1024 * 1024;

    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    // ─── 限额与截断 ───────────────────────────────────────────────────────────

    [Fact]
    public async Task AppendAsync_ShouldCapAtOutputLimit_AndMarkTruncated()
    {
        const long limit = 1024;
        var store = CreateStore(limit);
        var activityId = NewActivityId();

        var first = Fill('a', 800);
        var second = Fill('b', 800);

        await store.AppendAsync(activityId, first, CancellationToken.None);
        await store.AppendAsync(activityId, second, CancellationToken.None);

        var path = OutputPath(activityId);
        File.Exists(path).Should().BeTrue();

        // 文件 = 上限内的 1024 字节原始输出 + 一次截断标记。
        var onDisk = File.ReadAllBytes(path);
        var marker = Encoding.UTF8.GetBytes("\n[truncated]\n");
        onDisk.Length.Should().Be((int)limit + marker.Length);
        onDisk.AsSpan(0, 800).ToArray().Should().Equal(first);
        onDisk.AsSpan(800, 224).ToArray().Should().Equal(second[..224]);
        onDisk.AsSpan((int)limit).ToArray().Should().Equal(marker);

        // 超限后继续追加不得增长文件（标记只写一次）。
        await store.AppendAsync(activityId, Fill('c', 100), CancellationToken.None);
        (File.ReadAllBytes(path)).Length.Should().Be(onDisk.Length);

        var page = await store.ReadAsync(activityId, 0, 64 * 1024, CancellationToken.None);
        page.Truncated.Should().BeTrue();
        page.Content.Should().HaveLength((int)limit + marker.Length);
        page.Content.Should().EndWith("[truncated]\n");
        page.EndOfStream.Should().BeTrue();

        var stats = store.TryGetStats(activityId);
        stats.Should().NotBeNull();
        stats!.Value.TotalBytes.Should().Be(limit);
        stats.Value.Truncated.Should().BeTrue();
        stats.Value.LineCount.Should().Be(0);
        Encoding.UTF8.GetByteCount(stats.Value.Tail).Should().Be((int)limit);
    }

    // ─── 分页 ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReadAsync_ShouldPageByOffset_AndClampTo32KB()
    {
        var store = CreateStore(DefaultLimitBytes);
        var activityId = NewActivityId();

        const int total = 100_000;
        var payload = Pattern(total);
        await store.AppendAsync(activityId, payload, CancellationToken.None);
        var expected = Encoding.UTF8.GetString(payload);

        // 调用方传 1MB：实现必须钳制到 32KB。
        var first = await store.ReadAsync(activityId, 0, 1024 * 1024, CancellationToken.None);
        first.NextOffset.Should().Be(BackgroundActivityOutputStore.MaxPageBytes);
        first.Content.Should().Be(expected[..BackgroundActivityOutputStore.MaxPageBytes]);
        first.EndOfStream.Should().BeFalse();
        first.Truncated.Should().BeFalse();

        var second = await store.ReadAsync(activityId, first.NextOffset, 4096, CancellationToken.None);
        second.NextOffset.Should().Be(first.NextOffset + 4096);
        second.Content.Should().Be(expected[(int)first.NextOffset..(int)second.NextOffset]);
        second.EndOfStream.Should().BeFalse();

        // 按偏移连续读完，拼起来等于原文（游标单调前进，不会重复或丢字节）。
        var buffer = new StringBuilder();
        long offset = 0;
        while (true)
        {
            var page = await store.ReadAsync(activityId, offset, 8192, CancellationToken.None);
            buffer.Append(page.Content);
            offset = page.NextOffset;
            if (page.EndOfStream)
                break;
        }

        buffer.ToString().Should().Be(expected);
        offset.Should().Be(total);
    }

    [Fact]
    public async Task ReadAsync_ShouldNotSplitUtf8Sequence()
    {
        var store = CreateStore(DefaultLimitBytes);
        var activityId = NewActivityId();

        // 32767 个 ASCII + 2 字节的 'é'：32KB 边界恰好落在多字节字符中间。
        var payload = Encoding.UTF8.GetBytes(new string('a', 32_767) + "é");
        await store.AppendAsync(activityId, payload, CancellationToken.None);

        var first = await store.ReadAsync(activityId, 0, BackgroundActivityOutputStore.MaxPageBytes, CancellationToken.None);
        first.Content.Should().HaveLength(32_767);
        first.Content.Should().NotContain("\uFFFD");
        first.NextOffset.Should().Be(32_767);
        first.EndOfStream.Should().BeFalse();

        var second = await store.ReadAsync(activityId, first.NextOffset, BackgroundActivityOutputStore.MaxPageBytes, CancellationToken.None);
        second.Content.Should().Be("é");
        second.EndOfStream.Should().BeTrue();
        second.NextOffset.Should().Be(payload.Length);
    }

    [Fact]
    public async Task ReadAsync_ShouldReportEndOfStream()
    {
        var store = CreateStore(DefaultLimitBytes);
        var activityId = NewActivityId();
        const string text = "hello 世界";

        await store.AppendAsync(activityId, Encoding.UTF8.GetBytes(text), CancellationToken.None);

        var page = await store.ReadAsync(activityId, 0, 4096, CancellationToken.None);
        page.Content.Should().Be(text);
        page.NextOffset.Should().Be(Encoding.UTF8.GetByteCount(text));
        page.EndOfStream.Should().BeTrue();
        page.Truncated.Should().BeFalse();

        var atEnd = await store.ReadAsync(activityId, page.NextOffset, 4096, CancellationToken.None);
        atEnd.Content.Should().BeEmpty();
        atEnd.NextOffset.Should().Be(page.NextOffset);
        atEnd.EndOfStream.Should().BeTrue();

        var beyond = await store.ReadAsync(activityId, 10_000, 4096, CancellationToken.None);
        beyond.Content.Should().BeEmpty();
        beyond.NextOffset.Should().Be(10_000);
        beyond.EndOfStream.Should().BeTrue();
    }

    [Fact]
    public async Task ReadAsync_ShouldReportEndOfStream_WhenNoOutput()
    {
        var store = CreateStore(DefaultLimitBytes);

        var page = await store.ReadAsync(NewActivityId(), 0, 4096, CancellationToken.None);

        page.Content.Should().BeEmpty();
        page.NextOffset.Should().Be(0);
        page.EndOfStream.Should().BeTrue();
        page.Truncated.Should().BeFalse();
    }

    // ─── 路径校验 ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task InvalidActivityId_ShouldBeRejected()
    {
        var store = CreateStore(DefaultLimitBytes);
        var chunk = Encoding.UTF8.GetBytes("x");

        string[] invalidIds =
        [
            string.Empty,
            "act_",
            "act_" + new string('a', 31),
            "act_" + new string('a', 33),
            "act_" + new string('A', 32),
            "act_" + new string('g', 32),
            "act_" + new string('a', 31) + "/",
            "../act_" + new string('a', 32),
            "act_../../etc/passwd",
            "act_" + new string('a', 32) + "/../../escape"
        ];

        foreach (var activityId in invalidIds)
        {
            await FluentActions
                .Awaiting(() => store.AppendAsync(activityId, chunk, CancellationToken.None))
                .Should().ThrowAsync<ArgumentException>();

            await FluentActions
                .Awaiting(() => store.ReadAsync(activityId, 0, 4096, CancellationToken.None))
                .Should().ThrowAsync<ArgumentException>();

            await FluentActions
                .Awaiting(() => store.DeleteAsync(activityId))
                .Should().ThrowAsync<ArgumentException>();
        }

        // 非法 id 不得凭空造出活动根目录以外的东西。
        Directory.Exists(ActivityRoot()).Should().BeFalse();
    }

    // ─── 删除 ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAsync_ShouldBeIdempotent()
    {
        var store = CreateStore(DefaultLimitBytes);
        var activityId = NewActivityId();
        var path = OutputPath(activityId);

        await store.AppendAsync(activityId, Encoding.UTF8.GetBytes("abc"), CancellationToken.None);
        File.Exists(path).Should().BeTrue();

        await store.DeleteAsync(activityId);
        File.Exists(path).Should().BeFalse();

        var afterDelete = await store.ReadAsync(activityId, 0, 4096, CancellationToken.None);
        afterDelete.Content.Should().BeEmpty();
        afterDelete.EndOfStream.Should().BeTrue();

        // 第二次删除不抛异常。
        await store.DeleteAsync(activityId);

        // 删除后重新写入：状态被清理，按全新文件计（不会继承旧的截断标记）。
        await store.AppendAsync(activityId, Encoding.UTF8.GetBytes("xy"), CancellationToken.None);
        var afterRewrite = await store.ReadAsync(activityId, 0, 4096, CancellationToken.None);
        afterRewrite.Content.Should().Be("xy");
        afterRewrite.Truncated.Should().BeFalse();
        afterRewrite.EndOfStream.Should().BeTrue();
    }

    // ─── 工具 ─────────────────────────────────────────────────────────────────

    private BackgroundActivityOutputStore CreateStore(long outputLimitBytes)
    {
        var workspace = Mock.Of<IWorkspaceProvider>(
            w => w.ProjectSeeingDirectory == Path.Combine(_tempRoot, ".seeing"));

        return new BackgroundActivityOutputStore(
            new ProcessPathWriteLock(),
            workspace,
            new FixedOptionsMonitor<BackgroundActivityOptions>(
                new BackgroundActivityOptions { DefaultOutputLimitBytes = outputLimitBytes }),
            NullLogger<BackgroundActivityOutputStore>.Instance);
    }

    private string ActivityRoot() => Path.Combine(_tempRoot, ".seeing", BackgroundActivityOutputStore.DirectoryName);

    private string OutputPath(string activityId) => Path.Combine(ActivityRoot(), activityId + BackgroundActivityOutputStore.FileSuffix);

    private static string NewActivityId() => "act_" + Guid.NewGuid().ToString("N");

    private static byte[] Fill(char value, int length) => Encoding.ASCII.GetBytes(new string(value, length));

    private static byte[] Pattern(int length)
    {
        var buffer = new byte[length];
        for (var i = 0; i < length; i++)
            buffer[i] = (byte)('a' + (i % 26));

        return buffer;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch (Exception)
        {
            // 清理失败不影响断言结果。
        }
    }

    private sealed class FixedOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public FixedOptionsMonitor(T value) => CurrentValue = value;

        public T CurrentValue { get; }

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
