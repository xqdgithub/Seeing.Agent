using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Seeing.Agent.Hooks.Directory.Scripts;

public sealed class NodeHookClient : INodeHookInvoker
{
    public static readonly TimeSpan HookScriptTimeout = TimeSpan.FromSeconds(5);

    private readonly INodeRuntime _runtime;
    private readonly Func<string> _workerPath;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, PendingCall> _pending = new(StringComparer.Ordinal);
    private readonly object _availabilityGate = new();
    private readonly CancellationTokenSource _lifetime = new();

    private INodeProcess? _process;
    private long _generation;
    private long _activeGeneration;
    private int _nextId;
    private int _availability;
    private int _stopped;

    public NodeHookClient(INodeRuntime runtime, Func<string> workerPath, TimeProvider time, ILogger logger)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _workerPath = workerPath ?? throw new ArgumentNullException(nameof(workerPath));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool EnsureAvailable()
    {
        var known = Volatile.Read(ref _availability);
        if (known != 0)
            return known == 1;

        lock (_availabilityGate)
        {
            if (_availability != 0)
                return _availability == 1;

            bool ok;
            try
            {
                ok = _runtime.Probe();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "探测 Node 失败，本次进程不再执行钩子脚本。");
                _availability = 2;
                return false;
            }

            if (!ok)
            {
                _logger.LogWarning("未找到可用的 Node，本次进程不再执行钩子脚本。");
                _availability = 2;
                return false;
            }

            _availability = 1;
            return true;
        }
    }

    public async Task<ScriptInvocationResult> InvokeAsync(
        string scriptPath,
        JsonElement eventPayload,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptPath);

        // 排队不计入 5 秒。调用方取消也不能取消这次等待，否则排在后面的调用会被连坐跳过。
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _stopped) != 0)
                return ScriptInvocationResult.Failure("Node 进程已退出");

            if (cancellationToken.IsCancellationRequested)
                return ScriptInvocationResult.Cancel();

            // 超时源只看这 5 秒。调用方令牌另判，取消时不 Kill。
            using var timeoutCts = new CancellationTokenSource(HookScriptTimeout, _time);
            using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(
                timeoutCts.Token, cancellationToken, _lifetime.Token);

            INodeProcess process;
            try
            {
                process = EnsureProcess();
            }
            catch (Exception ex)
            {
                return ScriptInvocationResult.Failure(ex.Message);
            }

            if (Volatile.Read(ref _stopped) != 0)
            {
                Abandon(process);
                return ScriptInvocationResult.Failure("Node 进程已退出");
            }

            var id = Interlocked.Increment(ref _nextId).ToString(CultureInfo.InvariantCulture);
            var pending = new PendingCall(
                new TaskCompletionSource<ScriptInvocationResult>(TaskCreationOptions.RunContinuationsAsynchronously),
                _activeGeneration);
            _pending[id] = pending;

            var line = JsonSerializer.Serialize(
                new HookWorkerRequest { Id = id, Script = scriptPath, Event = eventPayload },
                HookJson.Options);

            try
            {
                await process.WriteLineAsync(line, operationCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _pending.TryRemove(id, out _);
                return FinishCanceledOr(ex, cancellationToken, process, timeoutCts.IsCancellationRequested);
            }

            try
            {
                return await pending.Completion.Task.WaitAsync(operationCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                _pending.TryRemove(id, out _);
                return FinishCanceledOr(ex, cancellationToken, process, timeoutCts.IsCancellationRequested);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // 宽限 2 秒在进程 DisposeAsync 里用 TimeProvider 计时，不跟调用方令牌走。
        _ = cancellationToken;
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
            return;

        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        var process = Interlocked.Exchange(ref _process, null);
        FailAll(ScriptInvocationResult.Failure("Node 进程已退出"));
        if (process is null)
            return;

        await process.DisposeAsync().ConfigureAwait(false);
    }

    private INodeProcess EnsureProcess()
    {
        var existing = _process;
        if (existing is not null)
            return existing;

        var process = _runtime.StartWorker(_workerPath());
        var generation = Interlocked.Increment(ref _generation);
        _activeGeneration = generation;
        _process = process;
        _ = ReadLoopAsync(process, generation);
        return process;
    }

    private ScriptInvocationResult FinishCanceledOr(
        Exception exception,
        CancellationToken cancellationToken,
        INodeProcess process,
        bool timedOut)
    {
        if (cancellationToken.IsCancellationRequested && exception is OperationCanceledException)
            return ScriptInvocationResult.Cancel();

        if (Volatile.Read(ref _stopped) != 0)
            return ScriptInvocationResult.Failure("Node 进程已退出");

        if (timedOut || exception is OperationCanceledException)
        {
            Abandon(process);
            return ScriptInvocationResult.Failure("timeout");
        }

        if (exception is IOException or ObjectDisposedException)
            Abandon(process);

        return ScriptInvocationResult.Failure(exception.Message);
    }

    private void Abandon(INodeProcess process)
    {
        Interlocked.CompareExchange(ref _process, null, process);
        process.Kill();
    }

    private async Task ReadLoopAsync(INodeProcess process, long generation)
    {
        try
        {
            while (true)
            {
                string? line;
                try
                {
                    line = await process.ReadLineAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "读取 Node 工人 stdout 失败。");
                    break;
                }

                if (line is null)
                    break;

                DispatchLine(line);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Node 工人读循环已退出。");
        }
        finally
        {
            FailGeneration(generation, ScriptInvocationResult.Failure("Node 进程已退出"));
            Interlocked.CompareExchange(ref _process, null, process);
            // Kill 只打断进程。等这一代已经离开 ReadLine 再释放，避免和正在读的 stdout 抢 Dispose，
            // 也不在调用锁里等生产进程最多 2 秒的退出宽限。
            await DisposeWorkerAsync(process).ConfigureAwait(false);
        }
    }

    private async Task DisposeWorkerAsync(INodeProcess process)
    {
        try
        {
            await process.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "释放 Node 工人进程失败。");
        }
    }

    private void DispatchLine(string line)
    {
        HookWorkerResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<HookWorkerResponse>(line, HookJson.Options);
        }
        catch (JsonException)
        {
            _logger.LogDebug("钩子工人输出了非 JSON 行。");
            return;
        }

        if (response is null || string.IsNullOrEmpty(response.Id))
        {
            _logger.LogDebug("钩子工人输出了非 JSON 行。");
            return;
        }

        if (!_pending.TryRemove(response.Id, out var pending))
        {
            _logger.LogDebug("钩子工人输出的 id {Id} 没有对应请求，已丢弃。", response.Id);
            return;
        }

        pending.Completion.TrySetResult(Map(response));
    }

    private static ScriptInvocationResult Map(HookWorkerResponse response)
    {
        if (!response.Ok)
        {
            return ScriptInvocationResult.Failure(
                string.IsNullOrEmpty(response.Error) ? "脚本失败" : response.Error);
        }

        JsonElement? mutable = response.Mutable.ValueKind == JsonValueKind.Undefined
            ? null
            : response.Mutable.Clone();
        return new ScriptInvocationResult(
            ScriptCallStatus.Succeeded,
            response.Continue ?? true,
            mutable,
            Error: null);
    }

    private void FailGeneration(long generation, ScriptInvocationResult result)
    {
        foreach (var pair in _pending)
        {
            if (pair.Value.Generation == generation && _pending.TryRemove(pair.Key, out var pending))
                pending.Completion.TrySetResult(result);
        }
    }

    private void FailAll(ScriptInvocationResult result)
    {
        foreach (var pair in _pending)
        {
            if (_pending.TryRemove(pair.Key, out var pending))
                pending.Completion.TrySetResult(result);
        }
    }

    private sealed class PendingCall(TaskCompletionSource<ScriptInvocationResult> completion, long generation)
    {
        public TaskCompletionSource<ScriptInvocationResult> Completion { get; } = completion;

        public long Generation { get; } = generation;
    }

    private sealed class HookWorkerRequest
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("script")]
        public required string Script { get; init; }

        [JsonPropertyName("event")]
        public required JsonElement Event { get; init; }
    }

    private sealed class HookWorkerResponse
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("continue")]
        public bool? Continue { get; set; }

        [JsonPropertyName("mutable")]
        public JsonElement Mutable { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }
}
