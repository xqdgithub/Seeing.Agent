using FluentAssertions;
using Seeing.IO.Local;
using Xunit;

namespace Seeing.Agent.Files.Tests;

/// <summary>验证文件工具与文件工作区共享同一进程内路径写锁（消除同进程并发写竞态）。</summary>
public class SharedWriteLockTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sharedlock-" + Guid.NewGuid().ToString("N"));

    public SharedWriteLockTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task FacadeAndTool_SharingLock_ShouldSerialize()
    {
        var shared = new ProcessPathWriteLock();
        var ws = new FileWorkspace("s1", _root, new LocalFileSystem(), new AllowAllGate(), new Watching.FileWatcherPool(), shared);
        var file = Path.Combine(_root, "a.txt");
        await ws.WriteAsync(file, "v0");

        var order = new List<string>();

        // 模拟文件工具持有同一路径锁进行读-改-写
        var toolTask = Task.Run(async () =>
        {
            using var _ = await shared.AcquireAsync(file, CancellationToken.None);
            lock (order) order.Add("tool-enter");
            await Task.Delay(80);
            await File.WriteAllTextAsync(file, "tool");
            lock (order) order.Add("tool-exit");
        });

        await Task.Delay(20); // 确保工具先拿到锁
        var facadeTask = Task.Run(async () =>
        {
            var r = await ws.WriteAsync(file, "facade");
            lock (order) order.Add("facade-done");
        });

        await Task.WhenAll(toolTask, facadeTask);

        // 工具的临界区必须完整先于 facade 写（不交错）
        order.Should().ContainInOrder("tool-enter", "tool-exit", "facade-done");
        (await File.ReadAllTextAsync(file)).Should().Be("facade");
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
