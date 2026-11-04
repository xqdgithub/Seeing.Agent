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
        var toolEntered = new SemaphoreSlim(0, 1);
        var toolMayExit = new SemaphoreSlim(0, 1);

        // 模拟文件工具持有同一路径锁进行读-改-写
        var toolTask = Task.Run(async () =>
        {
            using var _ = await shared.AcquireAsync(file, CancellationToken.None);
            lock (order) order.Add("tool-enter");
            // 明确等待「facade 已发起」再退出临界区，替代固定 sleep
            toolEntered.Release();
            await toolMayExit.WaitAsync(TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(file, "tool", TestContext.Current.CancellationToken);
            lock (order) order.Add("tool-exit");
        });

        await toolEntered.WaitAsync(TestContext.Current.CancellationToken);
        var facadeTask = Task.Run(async () =>
        {
            var r = await ws.WriteAsync(file, "facade");
            lock (order) order.Add("facade-done");
        });

        // facade 已在等锁；让工具退出，验证临界区串行而非交错
        toolMayExit.Release();
        await Task.WhenAll(toolTask, facadeTask);

        // 工具的临界区必须完整先于 facade 写（不交错）
        order.Should().ContainInOrder("tool-enter", "tool-exit", "facade-done");
        (await File.ReadAllTextAsync(file)).Should().Be("facade");
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
