using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Seeing.Agent.Hooks.Directory.Tests;

public class HookDirectoryWatcherTests : IDisposable
{
    private readonly string _root;

    public HookDirectoryWatcherTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "seeing-hook-watch-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (System.IO.Directory.Exists(_root))
                System.IO.Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 监听器释放后目录仍可能短暂被占用。
        }
    }

    [Fact]
    public async Task ArmForTests_WhileRescanInFlight_ShouldRunOneFollowUpAndStop()
    {
        var calls = 0;
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ct = TestContext.Current.CancellationToken;

        using var watcher = new HookDirectoryWatcher(
            _root,
            _ =>
            {
                var n = Interlocked.Increment(ref calls);
                if (n == 1)
                {
                    firstEntered.TrySetResult();
                    return releaseFirst.Task;
                }

                secondEntered.TrySetResult();
                return Task.CompletedTask;
            },
            TimeProvider.System,
            NullLogger.Instance);

        watcher.ArmForTests();
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        watcher.ArmForTests();
        calls.Should().Be(1);

        releaseFirst.TrySetResult();
        await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        await Task.Yield();
        calls.Should().Be(2);
    }

    [Fact]
    public void ArmForTests_AfterDispose_ShouldNotRescan()
    {
        var calls = 0;
        var watcher = new HookDirectoryWatcher(
            _root,
            _ =>
            {
                Interlocked.Increment(ref calls);
                return Task.CompletedTask;
            },
            TimeProvider.System,
            NullLogger.Instance);

        watcher.Dispose();
        watcher.ArmForTests();

        calls.Should().Be(0);
    }
}
