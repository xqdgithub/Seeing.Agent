using FluentAssertions;
using Seeing.Agent.Files.Watching;
using Xunit;

namespace Seeing.Agent.Files.Tests;

public class FileWatcherPoolTests
{
    [Fact]
    public void SameRoot_ShouldShareOneWatcher()
    {
        var root = Path.Combine(Path.GetTempPath(), "wpool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var pool = new FileWatcherPool();
            var a = pool.Subscribe(root);
            var b = pool.Subscribe(root);

            pool.RootCount.Should().Be(1);

            a.Dispose();
            pool.RootCount.Should().Be(1);

            var c = pool.Subscribe(root);
            b.Dispose();
            c.Dispose();
            pool.RootCount.Should().Be(0);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void DistinctRoots_ShouldPoolSeparately()
    {
        var root1 = Path.Combine(Path.GetTempPath(), "wpool-" + Guid.NewGuid().ToString("N"));
        var root2 = Path.Combine(Path.GetTempPath(), "wpool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root1);
        Directory.CreateDirectory(root2);
        try
        {
            using var pool = new FileWatcherPool();
            var a = pool.Subscribe(root1);
            var b = pool.Subscribe(root2);

            pool.RootCount.Should().Be(2);

            a.Dispose();
            b.Dispose();
            pool.RootCount.Should().Be(0);
        }
        finally { Directory.Delete(root1, true); Directory.Delete(root2, true); }
    }

    [Fact]
    public void ReleaseRoot_UnknownRoot_ShouldBeNoOp()
    {
        using var pool = new FileWatcherPool();
        var act = () => pool.ReleaseRoot(Path.Combine(Path.GetTempPath(), "no-such-root-xyz"));

        act.Should().NotThrow();
    }
}
