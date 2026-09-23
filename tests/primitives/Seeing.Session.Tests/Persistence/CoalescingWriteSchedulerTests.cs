using System.Collections.Concurrent;
using System.Diagnostics;
using FluentAssertions;
using Seeing.Session.Persistence;
using Xunit;

namespace Seeing.Session.Tests.Persistence;

/// <summary>
/// <see cref="CoalescingWriteScheduler{TKey,TValue}"/> 语义测试（设计规格 §4 / §9）。
/// 全部使用短窗口真实时间，避免长时间等待。
/// </summary>
public sealed class CoalescingWriteSchedulerTests
{
    private static CoalescingWriteScheduler<string, string> Create(
        Func<string, CancellationToken, Task> write,
        TimeSpan? debounce = null,
        TimeSpan? maxFlushDelay = null,
        TimeSpan? maxRetryBackoff = null,
        TimeSpan? shutdownFlushTimeout = null)
        => new(
            debounce ?? TimeSpan.FromMilliseconds(60),
            maxFlushDelay ?? TimeSpan.FromMilliseconds(600),
            maxRetryBackoff ?? TimeSpan.FromMilliseconds(200),
            shutdownFlushTimeout ?? TimeSpan.FromSeconds(2),
            write);

    [Fact]
    public async Task Enqueue_MultipleWithinWindow_ShouldWriteOnceAndLastWriterWins()
    {
        var recorder = new WriteRecorder();
        await using var scheduler = Create(recorder.Write, debounce: TimeSpan.FromMilliseconds(80), maxFlushDelay: TimeSpan.FromSeconds(2));

        for (var i = 0; i < 10; i++)
            scheduler.Enqueue("s1", $"v{i}");

        await recorder.WaitForCountAsync(1, TimeSpan.FromSeconds(3));
        await Task.Delay(250, TestContext.Current.CancellationToken);

        recorder.Count.Should().Be(1);
        recorder.Values.Should().ContainSingle().Which.Should().Be("v9");
    }

    [Fact]
    public async Task Enqueue_WithinDebounceWindow_ShouldResetTrailingWindow()
    {
        var recorder = new WriteRecorder();
        await using var scheduler = Create(recorder.Write, debounce: TimeSpan.FromMilliseconds(300), maxFlushDelay: TimeSpan.FromSeconds(3));

        scheduler.Enqueue("s1", "a");
        await Task.Delay(120, TestContext.Current.CancellationToken);
        scheduler.Enqueue("s1", "b");

        // 尚未越过重置后的静默窗口
        recorder.Count.Should().Be(0);

        await recorder.WaitForCountAsync(1, TimeSpan.FromSeconds(3));
        recorder.Values.Should().ContainSingle().Which.Should().Be("b");
    }

    [Fact]
    public async Task Enqueue_ContinuousWrites_ShouldRespectStarvationGuard()
    {
        var recorder = new WriteRecorder();
        await using var scheduler = Create(
            recorder.Write,
            debounce: TimeSpan.FromSeconds(5),
            maxFlushDelay: TimeSpan.FromMilliseconds(150),
            maxRetryBackoff: TimeSpan.FromMilliseconds(200));

        for (var i = 0; i < 10; i++)
        {
            scheduler.Enqueue("s1", $"v{i}");
            await Task.Delay(40, TestContext.Current.CancellationToken);
        }

        // 持续的 Enqueue 仍应在 MaxFlushDelay 内被强制落盘至少一次
        recorder.Count.Should().BeGreaterThan(0);

        await scheduler.FlushAsync("s1", TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        recorder.Values.Should().Contain("v9");
    }

    [Fact]
    public async Task Enqueue_WhenWriteFails_ShouldNotPropagateAndRetryUntilSuccess()
    {
        var attempts = 0;
        var recorder = new WriteRecorder();
        Func<string, CancellationToken, Task> write = (value, _) =>
        {
            if (Interlocked.Increment(ref attempts) <= 2)
                return Task.FromException(new IOException("boom"));
            recorder.Record(value);
            return Task.CompletedTask;
        };

        await using var scheduler = Create(
            write,
            debounce: TimeSpan.FromMilliseconds(40),
            maxFlushDelay: TimeSpan.FromMilliseconds(300),
            maxRetryBackoff: TimeSpan.FromMilliseconds(80));

        var enqueue = () => scheduler.Enqueue("s1", "v");
        enqueue.Should().NotThrow();

        // Enqueue 不向调用方传播失败；后台按退避重试，直至成功落盘
        await recorder.WaitForCountAsync(1, TimeSpan.FromSeconds(5));

        attempts.Should().BeGreaterThanOrEqualTo(3);
        recorder.Values.Should().ContainSingle().Which.Should().Be("v");
    }

    [Fact]
    public async Task TryFlushAsync_WhenWriteKeepsFailing_ShouldReturnFalseBounded()
    {
        Func<string, CancellationToken, Task> write = (_, _) => Task.FromException(new IOException("boom"));
        await using var scheduler = Create(
            write,
            debounce: TimeSpan.FromMilliseconds(30),
            maxFlushDelay: TimeSpan.FromMilliseconds(200),
            maxRetryBackoff: TimeSpan.FromMilliseconds(50),
            shutdownFlushTimeout: TimeSpan.FromMilliseconds(200));

        scheduler.Enqueue("s1", "v");

        var stopwatch = Stopwatch.StartNew();
        var ok = await scheduler.TryFlushAsync("s1", TimeSpan.FromMilliseconds(150), TestContext.Current.CancellationToken);
        stopwatch.Stop();

        ok.Should().BeFalse();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task FlushAsync_ShouldWaitUntilWriteCompletes()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recorder = new WriteRecorder();
        Func<string, CancellationToken, Task> write = async (value, _) =>
        {
            await gate.Task;
            recorder.Record(value);
        };

        await using var scheduler = Create(write, debounce: TimeSpan.FromMilliseconds(20), maxFlushDelay: TimeSpan.FromMilliseconds(300));

        scheduler.Enqueue("s1", "v");
        var flush = scheduler.FlushAsync("s1", TestContext.Current.CancellationToken);

        flush.IsCompleted.Should().BeFalse();

        gate.SetResult();
        await flush.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        recorder.Values.Should().ContainSingle().Which.Should().Be("v");
    }

    [Fact]
    public async Task TryFlushAsync_WhenNothingPending_ShouldReturnTrueImmediately()
    {
        await using var scheduler = Create((_, _) => Task.CompletedTask);

        var ok = await scheduler.TryFlushAsync("missing", TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);

        ok.Should().BeTrue();
    }

    [Fact]
    public async Task TryFlushAllAsync_WhenNothingPending_ShouldReturnTrue()
    {
        await using var scheduler = Create((_, _) => Task.CompletedTask);

        var ok = await scheduler.TryFlushAllAsync(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);

        ok.Should().BeTrue();
    }

    [Fact]
    public async Task DiscardAsync_ShouldWaitForInFlightWrite()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recorder = new WriteRecorder();
        Func<string, CancellationToken, Task> write = async (value, _) =>
        {
            started.TrySetResult();
            await release.Task;
            recorder.Record(value);
        };

        await using var scheduler = Create(write, debounce: TimeSpan.FromMilliseconds(20), maxFlushDelay: TimeSpan.FromMilliseconds(300));

        scheduler.Enqueue("s1", "v");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        var discard = scheduler.DiscardAsync("s1", TestContext.Current.CancellationToken);
        discard.IsCompleted.Should().BeFalse();

        release.SetResult();
        await discard.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Enqueue_AfterDiscard_ShouldClearTombstoneAndPersistAgain()
    {
        var recorder = new WriteRecorder();
        await using var scheduler = Create(recorder.Write, debounce: TimeSpan.FromMilliseconds(30), maxFlushDelay: TimeSpan.FromMilliseconds(300));

        scheduler.Enqueue("s1", "v1");
        await scheduler.FlushAsync("s1", TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        await scheduler.DiscardAsync("s1", TestContext.Current.CancellationToken);

        scheduler.Enqueue("s1", "v2");
        await scheduler.FlushAsync("s1", TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        recorder.Values.Should().ContainInOrder("v1", "v2");
    }

    [Fact]
    public async Task DisposeAsync_ShouldFlushPendingWrites()
    {
        var recorder = new WriteRecorder();
        var scheduler = Create(
            recorder.Write,
            debounce: TimeSpan.FromSeconds(10),
            maxFlushDelay: TimeSpan.FromSeconds(10));

        scheduler.Enqueue("s1", "v");
        await scheduler.DisposeAsync();

        recorder.Values.Should().ContainSingle().Which.Should().Be("v");
    }

    [Fact]
    public void Dispose_ShouldFlushPendingWrites()
    {
        var recorder = new WriteRecorder();
        var scheduler = Create(
            recorder.Write,
            debounce: TimeSpan.FromSeconds(10),
            maxFlushDelay: TimeSpan.FromSeconds(10));

        scheduler.Enqueue("s1", "v");
        scheduler.Dispose();

        recorder.Values.Should().ContainSingle().Which.Should().Be("v");
    }

    [Fact]
    public void Dispose_WhenWriteKeepsFailing_ShouldNotThrow()
    {
        Func<string, CancellationToken, Task> write = (_, _) => Task.FromException(new IOException("boom"));
        var scheduler = Create(
            write,
            debounce: TimeSpan.FromMilliseconds(10),
            maxFlushDelay: TimeSpan.FromMilliseconds(30),
            maxRetryBackoff: TimeSpan.FromMilliseconds(20),
            shutdownFlushTimeout: TimeSpan.FromMilliseconds(200));

        scheduler.Enqueue("s1", "v");

        // 释放路径必须 best-effort：最终 flush 持续失败也不得向 Dispose 调用方抛出
        var act = () => scheduler.Dispose();

        act.Should().NotThrow();
    }

    [Fact]
    public async Task DisposeAsync_WhenWriteKeepsFailing_ShouldNotThrow()
    {
        Func<string, CancellationToken, Task> write = (_, _) => Task.FromException(new IOException("boom"));
        var scheduler = Create(
            write,
            debounce: TimeSpan.FromMilliseconds(10),
            maxFlushDelay: TimeSpan.FromMilliseconds(30),
            maxRetryBackoff: TimeSpan.FromMilliseconds(20),
            shutdownFlushTimeout: TimeSpan.FromMilliseconds(200));

        scheduler.Enqueue("s1", "v");

        Func<Task> act = () => scheduler.DisposeAsync().AsTask();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task FlushAsync_WhenWriteKeepsFailing_ShouldThrowWithinBound()
    {
        Func<string, CancellationToken, Task> write = (_, _) => Task.FromException(new IOException("boom"));
        await using var scheduler = Create(
            write,
            debounce: TimeSpan.FromMilliseconds(10),
            maxFlushDelay: TimeSpan.FromMilliseconds(50),
            maxRetryBackoff: TimeSpan.FromMilliseconds(50),
            shutdownFlushTimeout: TimeSpan.FromMilliseconds(200));

        scheduler.Enqueue("s1", "v");

        var stopwatch = Stopwatch.StartNew();
        Func<Task> act = () => scheduler.FlushAsync("s1");

        // 必须快速抛出（而非永久挂起）；WaitAsync 超时即视为失败
        var assertion = await act.Should().ThrowAsync<Exception>().WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        stopwatch.Stop();

        assertion.WithMessage("*持久化写入失败*s1*");
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Enqueue_SustainedContinuousWrites_ShouldKeepWriteCountBounded()
    {
        var writes = 0;
        using var stop = new CancellationTokenSource();
        await using var scheduler = Create(
            async (_, _) =>
            {
                Interlocked.Increment(ref writes);
                await Task.Yield();
            },
            debounce: TimeSpan.FromMilliseconds(10),
            maxFlushDelay: TimeSpan.FromMilliseconds(50),
            maxRetryBackoff: TimeSpan.FromMilliseconds(20));

        var enqueueTask = Task.Run(async () =>
        {
            var i = 0;
            while (!stop.IsCancellationRequested)
            {
                scheduler.Enqueue("s1", $"v{i++}");
                await Task.Delay(1);
            }
        }, TestContext.Current.CancellationToken);

        await Task.Delay(500, TestContext.Current.CancellationToken);
        stop.Cancel();
        await enqueueTask;

        await scheduler.FlushAsync("s1", TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        // 尾沿去抖 + 饥饿保护生效时应约为 duration / MaxFlushDelay（~10 次），
        // 若退化为“每次 Enqueue 各写一次”，计数会远超此上限。
        writes.Should().BeGreaterThan(0);
        writes.Should().BeLessThan(100);
    }

    [Fact]
    public async Task DiscardAsync_ConcurrentWithEnqueue_ShouldNotDropNewValue()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recorder = new WriteRecorder();
        Func<string, CancellationToken, Task> write = async (value, _) =>
        {
            if (value == "v1")
            {
                started.TrySetResult();
                await release.Task;
            }

            recorder.Record(value);
        };

        await using var scheduler = Create(write, debounce: TimeSpan.FromMilliseconds(10), maxFlushDelay: TimeSpan.FromMilliseconds(200));

        scheduler.Enqueue("s1", "v1");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken); // v1 在途且被阻塞

        // DiscardAsync 同步执行到“等待在途写”这一步后才挂起，此时墓碑已置位
        var discard = scheduler.DiscardAsync("s1", TestContext.Current.CancellationToken);
        // 等待期间合法重建：清除墓碑并入队新快照
        scheduler.Enqueue("s1", "v2");

        release.SetResult();
        await discard.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await scheduler.FlushAsync("s1", TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        recorder.Values.Should().Contain("v2");
    }

    [Fact]
    public async Task FlushAsync_ConcurrentWithBackgroundFlush_ShouldNotHang()
    {
        var recorder = new WriteRecorder();
        await using var scheduler = Create(
            recorder.Write,
            debounce: TimeSpan.FromMilliseconds(5),
            maxFlushDelay: TimeSpan.FromMilliseconds(30),
            maxRetryBackoff: TimeSpan.FromMilliseconds(20));

        var workers = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            var key = $"s{i}";
            for (var j = 0; j < 100; j++)
            {
                scheduler.Enqueue(key, $"v{j}");
                await scheduler.FlushAsync(key);
            }
        })).ToArray();

        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
    }

    private sealed class WriteRecorder
    {
        private readonly ConcurrentQueue<string> _values = new();

        public IReadOnlyCollection<string> Values => _values;

        public int Count => _values.Count;

        public Task Write(string value, CancellationToken cancellationToken)
        {
            _values.Enqueue(value);
            return Task.CompletedTask;
        }

        public void Record(string value) => _values.Enqueue(value);

        public async Task WaitForCountAsync(int count, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (_values.Count < count && DateTime.UtcNow < deadline)
                await Task.Delay(10);
        }
    }
}
