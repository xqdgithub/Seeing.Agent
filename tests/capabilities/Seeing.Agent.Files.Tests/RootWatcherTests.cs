using System.Collections.Concurrent;
using System.Threading.Channels;
using FluentAssertions;
using Seeing.Agent.Abstractions.Files;
using Seeing.Agent.Files.Watching;
using Xunit;

namespace Seeing.Agent.Files.Tests;

public class RootWatcherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rootwatch-" + Guid.NewGuid().ToString("N"));

    public RootWatcherTests() => Directory.CreateDirectory(_root);

    private static async Task<List<RootWatcher.RawChange>> CollectAsync(
        ChannelReader<RootWatcher.RawChange> reader, TimeSpan timeout)
    {
        var collected = new List<RootWatcher.RawChange>();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            while (await reader.WaitToReadAsync(cts.Token).ConfigureAwait(false))
                while (reader.TryRead(out var raw))
                    collected.Add(raw);
        }
        catch (OperationCanceledException) { }
        return collected;
    }

    [Fact]
    public async Task WriteSmallFile_ShouldEmitEvent()
    {
        using var pool = new FileWatcherPool();
        using var sub = pool.Subscribe(_root);
        var file = Path.Combine(_root, "small.txt");

        await File.WriteAllTextAsync(file, "hello");

        var events = await CollectAsync(sub.Reader, TimeSpan.FromSeconds(3));

        events.Should().Contain(e => e.Path == file
            && (e.Kind == FileChangeKind.Created || e.Kind == FileChangeKind.Modified));
    }

    [Fact]
    public async Task IgnoredDir_ShouldProduceNoEvent()
    {
        using var pool = new FileWatcherPool();
        using var sub = pool.Subscribe(_root);
        var gitDir = Path.Combine(_root, ".git");
        Directory.CreateDirectory(gitDir);
        await File.WriteAllTextAsync(Path.Combine(gitDir, "HEAD"), "ref: x");

        // 先制造一个正常事件作为“观察窗口结束”的信号，确保忽略目录的事件有机会被消费。
        var normal = Path.Combine(_root, "normal.txt");
        await File.WriteAllTextAsync(normal, "ok");

        var events = await CollectAsync(sub.Reader, TimeSpan.FromSeconds(3));

        events.Should().Contain(e => e.Path == normal);
        events.Should().NotContain(e => e.Path.Contains(".git", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MultipleWrites_ShouldNotEmitFromUnstableRead()
    {
        using var pool = new FileWatcherPool();
        using var sub = pool.Subscribe(_root);
        var file = Path.Combine(_root, "growing.txt");

        // 先创建，再持续追加，验证最终仍会收到稳定后的 Modified/Created。
        await File.WriteAllTextAsync(file, "a");
        for (var i = 0; i < 5; i++)
        {
            await File.AppendAllTextAsync(file, new string('x', 1024));
            await Task.Delay(50);
        }

        var events = await CollectAsync(sub.Reader, TimeSpan.FromSeconds(3));

        events.Should().Contain(e => e.Path == file);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
