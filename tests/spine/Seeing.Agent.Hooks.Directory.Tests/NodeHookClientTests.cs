using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Seeing.Agent.Hooks.Directory.Scripts;
using Xunit;

namespace Seeing.Agent.Hooks.Directory.Tests;

public class NodeHookClientTests
{
    private const string ScriptPath = @"E:\hooks\tool.execute.before.js";

    [Fact]
    public void HookScriptTimeout_ShouldBeFiveSeconds()
    {
        NodeHookClient.HookScriptTimeout.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task InvokeAsync_NonJsonAndForeignId_ShouldSucceedOnMatchingRequest()
    {
        var clock = new FakeTimeProvider();
        var runtime = new FakeNodeRuntime();
        var logs = new ListLogger();
        var client = Create(runtime, clock, logs);

        var call = client.InvokeAsync(ScriptPath, Event(), CancellationToken.None);

        runtime.StartCount.Should().Be(1);
        var process = runtime.Processes.Single();
        var request = process.Written.Should().ContainSingle().Subject;
        using (var doc = JsonDocument.Parse(request))
        {
            var names = doc.RootElement.EnumerateObject().Select(property => property.Name).ToList();
            names.Should().Contain("id");
            names.Should().Contain("script");
            names.Should().Contain("event");
            names.Should().OnlyContain(name => name == name.ToLowerInvariant());
            doc.RootElement.GetProperty("script").GetString().Should().Be(ScriptPath);
            doc.RootElement.GetProperty("event").GetProperty("point").GetString().Should().Be("tool.execute.before");
        }

        var id = IdOf(request);
        process.Enqueue("not json");
        process.Enqueue("{\"id\":\"其他\",\"ok\":true}");
        process.Enqueue(Ok(id));

        var result = await call;

        result.Status.Should().Be(ScriptCallStatus.Succeeded);
        result.Continue.Should().BeTrue();
        logs.Debugs.Should().Contain(message => message.Contains("非 JSON", StringComparison.Ordinal));
        logs.Debugs.Should().Contain(message => message.Contains("其他", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvokeAsync_TimeoutWhileAnotherWaits_ShouldKillAndLetWaiterStartFresh()
    {
        var clock = new FakeTimeProvider();
        var runtime = new FakeNodeRuntime();
        var workerPaths = 0;
        var client = new NodeHookClient(
            runtime,
            () =>
            {
                workerPaths++;
                return @"E:\cache\hook-worker.js";
            },
            clock,
            new ListLogger());

        var first = client.InvokeAsync(ScriptPath, Event(), CancellationToken.None);
        runtime.StartCount.Should().Be(1);
        var process1 = runtime.Processes[0];
        process1.Written.Should().ContainSingle();

        var second = client.InvokeAsync(@"E:\hooks\next.js", Event(), CancellationToken.None);
        runtime.StartCount.Should().Be(1);

        await AdvanceAsync(clock, NodeHookClient.HookScriptTimeout);

        var firstResult = await first;
        firstResult.Status.Should().Be(ScriptCallStatus.Failed);
        firstResult.Error.Should().Be("timeout");
        process1.KillCount.Should().Be(1);

        var winner = await Task.WhenAny(second, runtime.SecondProcess);
        if (winner == second && runtime.Processes.Count < 2)
        {
            (await second).Status.Should().Be(ScriptCallStatus.Succeeded);
        }

        var process2 = await runtime.SecondProcess;
        process2.Should().NotBeSameAs(process1);
        var line = await process2.ReadWrittenAsync();
        using (var doc = JsonDocument.Parse(line))
        {
            doc.RootElement.GetProperty("script").GetString().Should().Be(@"E:\hooks\next.js");
        }

        process2.Enqueue(Ok(IdOf(line)));
        var secondResult = await second;

        secondResult.Status.Should().Be(ScriptCallStatus.Succeeded);
        workerPaths.Should().Be(2);
        runtime.StartCount.Should().Be(2);
    }

    [Fact]
    public async Task InvokeAsync_CallerCanceled_ShouldCancelWithoutKillAndPairNextId()
    {
        var clock = new FakeTimeProvider();
        var runtime = new FakeNodeRuntime();
        var client = Create(runtime, clock, new ListLogger());
        using var cts = new CancellationTokenSource();

        var first = client.InvokeAsync(ScriptPath, Event(), cts.Token);
        var process = runtime.Processes.Single();
        var staleId = IdOf(process.Written.Single());

        cts.Cancel();
        var canceled = await first;

        canceled.Status.Should().Be(ScriptCallStatus.Canceled);
        canceled.Continue.Should().BeTrue();
        canceled.Mutable.Should().BeNull();
        canceled.Error.Should().BeNull();
        process.KillCount.Should().Be(0);
        runtime.StartCount.Should().Be(1);

        process.Enqueue(Ok(staleId));
        await Task.Yield();

        var second = client.InvokeAsync(@"E:\hooks\next.js", Event(), CancellationToken.None);
        await Task.Yield();

        second.IsCompleted.Should().BeFalse();
        runtime.StartCount.Should().Be(1);
        process.KillCount.Should().Be(0);
        var newId = IdOf(process.Written.Should().HaveCount(2).And.Subject.Last());
        newId.Should().NotBe(staleId);

        process.Enqueue(Ok(newId));
        var next = await second;

        next.Status.Should().Be(ScriptCallStatus.Succeeded);
        next.Continue.Should().BeTrue();
        runtime.StartCount.Should().Be(1);
    }

    [Fact]
    public void EnsureAvailable_ProbeFails_ShouldStayFalseAndNotStartWorker()
    {
        var runtime = new FakeNodeRuntime { ProbeResult = false };
        var logs = new ListLogger();
        var client = Create(runtime, new FakeTimeProvider(), logs);

        client.EnsureAvailable().Should().BeFalse();
        client.EnsureAvailable().Should().BeFalse();

        runtime.ProbeCount.Should().Be(1);
        runtime.StartCount.Should().Be(0);
        logs.Warnings.Should().ContainSingle();
    }

    [Fact]
    public void Ensure_ContentHash_ShouldReuseMatchingBytesAndRewriteMismatch()
    {
        var cache = Path.Combine(Path.GetTempPath(), "seeing-hook-worker-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = HookWorkerFiles.Ensure(cache);
            var original = File.ReadAllBytes(first);
            Path.GetFileName(first).Should().Be("hook-worker-" + Hash(original) + ".js");
            File.ReadAllText(first).Should().Contain("realStdoutWrite");

            HookWorkerFiles.Ensure(cache).Should().Be(first);

            var corrupted = original.ToArray();
            corrupted[0] ^= 0xFF;
            File.WriteAllBytes(first, corrupted);
            var rewritten = HookWorkerFiles.Ensure(cache);

            rewritten.Should().Be(first);
            File.ReadAllBytes(rewritten).Should().Equal(original);
        }
        finally
        {
            if (System.IO.Directory.Exists(cache))
                System.IO.Directory.Delete(cache, recursive: true);
        }
    }

    private static async Task AdvanceAsync(FakeTimeProvider clock, TimeSpan delta)
    {
        clock.Advance(delta);
        await Task.Yield();
    }

    private static NodeHookClient Create(FakeNodeRuntime runtime, FakeTimeProvider clock, ListLogger logger) =>
        new(runtime, () => @"E:\cache\hook-worker.js", clock, logger);

    private static JsonElement Event()
    {
        using var doc = JsonDocument.Parse("{\"point\":\"tool.execute.before\"}");
        return doc.RootElement.Clone();
    }

    private static string Ok(string id) =>
        "{\"id\":" + JsonSerializer.Serialize(id) + ",\"ok\":true,\"continue\":true}";

    private static string IdOf(string line)
    {
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.GetProperty("id").GetString()!;
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class FakeNodeRuntime : INodeRuntime
    {
        private readonly Channel<FakeNodeProcess> _started = Channel.CreateUnbounded<FakeNodeProcess>();
        private readonly TaskCompletionSource<FakeNodeProcess> _second =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool ProbeResult { get; set; } = true;

        public int ProbeCount { get; private set; }

        public int StartCount { get; private set; }

        public List<FakeNodeProcess> Processes { get; } = [];

        public Task<FakeNodeProcess> SecondProcess => _second.Task;

        public bool Probe()
        {
            ProbeCount++;
            return ProbeResult;
        }

        public INodeProcess StartWorker(string workerPath)
        {
            StartCount++;
            var process = new FakeNodeProcess();
            Processes.Add(process);
            _started.Writer.TryWrite(process);
            if (Processes.Count == 2)
                _second.TrySetResult(process);
            return process;
        }
    }

    private sealed class FakeNodeProcess : INodeProcess
    {
        private readonly Channel<string> _stdout = Channel.CreateUnbounded<string>();
        private readonly Channel<string> _written = Channel.CreateUnbounded<string>();

        public List<string> Written { get; } = [];

        public int KillCount { get; private set; }

        public Task WriteLineAsync(string line, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Written.Add(line);
            _written.Writer.TryWrite(line);
            return Task.CompletedTask;
        }

        public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            while (await _stdout.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_stdout.Reader.TryRead(out var line))
                    return line;
            }

            return null;
        }

        public ValueTask<string> ReadWrittenAsync() => _written.Reader.ReadAsync();

        public void Enqueue(string line) => _stdout.Writer.TryWrite(line);

        public void Kill()
        {
            KillCount++;
            _stdout.Writer.TryComplete();
        }

        public ValueTask DisposeAsync()
        {
            _stdout.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ListLogger : ILogger
    {
        public List<string> Debugs { get; } = [];

        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (logLevel == LogLevel.Debug)
                Debugs.Add(message);
            else if (logLevel == LogLevel.Warning)
                Warnings.Add(message);
        }
    }
}
