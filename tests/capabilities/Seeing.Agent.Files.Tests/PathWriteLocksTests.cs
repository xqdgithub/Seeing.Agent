using FluentAssertions;
using Seeing.Agent.Files;
using Xunit;

namespace Seeing.Agent.Files.Tests;

public class PathWriteLocksTests
{
    [Fact]
    public async Task SamePath_ShouldSerialize()
    {
        var locks = new PathWriteLocks();
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
        var locks = new PathWriteLocks();

        using var a = await locks.AcquireAsync("a.txt", CancellationToken.None);
        var bTask = locks.AcquireAsync("b.txt", CancellationToken.None);

        var completed = await Task.WhenAny(bTask, Task.Delay(1000));
        completed.Should().BeSameAs(bTask);
        (await bTask).Dispose();
    }

    [Fact]
    public async Task Release_ShouldAllowReacquire()
    {
        var locks = new PathWriteLocks();

        using (await locks.AcquireAsync("a.txt", CancellationToken.None)) { }
        using var again = await locks.AcquireAsync("a.txt", CancellationToken.None);

        again.Should().NotBeNull();
    }
}
