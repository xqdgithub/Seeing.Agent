using FluentAssertions;
using Seeing.IO.Local;
using Xunit;

namespace Seeing.Agent.Files.Tests;

public class ProcessPathWriteLockTests
{
    [Fact]
    public async Task SamePath_ShouldSerialize()
    {
        var locks = new ProcessPathWriteLock();
        var order = new List<string>();

        var first = await locks.AcquireAsync("a.txt", CancellationToken.None);
        var secondTask = Task.Run(async () =>
        {
            using var _ = await locks.AcquireAsync("a.txt", CancellationToken.None);
            lock (order) order.Add("second");
        });

        await Task.Delay(50);
        lock (order) order.Add("first-release");
        first.Dispose();
        await secondTask;

        order.Should().ContainInOrder("first-release", "second");
    }

    [Fact]
    public async Task DifferentPaths_ShouldNotBlock()
    {
        var locks = new ProcessPathWriteLock();

        using var a = await locks.AcquireAsync("a.txt", CancellationToken.None);
        var bTask = locks.AcquireAsync("b.txt", CancellationToken.None);

        var completed = await Task.WhenAny(bTask, Task.Delay(1000));
        completed.Should().BeSameAs(bTask);
        (await bTask).Dispose();
    }

    [Fact]
    public async Task Release_ShouldAllowReacquire()
    {
        var locks = new ProcessPathWriteLock();

        using (await locks.AcquireAsync("a.txt", CancellationToken.None)) { }
        using var again = await locks.AcquireAsync("a.txt", CancellationToken.None);

        again.Should().NotBeNull();
    }

    [Fact]
    public async Task CancelledAcquire_ShouldNotLeakLock()
    {
        var locks = new ProcessPathWriteLock();
        using var held = await locks.AcquireAsync("a.txt", CancellationToken.None);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var act = async () => await locks.AcquireAsync("a.txt", cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        // 释放后应能重新获取（引用计数未泄漏导致无法移除/获取）
        held.Dispose();
        using var again = await locks.AcquireAsync("a.txt", CancellationToken.None);
        again.Should().NotBeNull();
    }
}
