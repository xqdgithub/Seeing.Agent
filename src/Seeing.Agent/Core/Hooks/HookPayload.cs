using System.Collections.ObjectModel;

namespace Seeing.Agent.Core.Hooks;

public class HookPayload
{
    private static readonly IReadOnlyDictionary<string, object?> EmptyInput =
        new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>());

    private static readonly IReadOnlyDictionary<string, object?> EmptyResult = EmptyInput;

    public HookSpec Spec { get; init; } = HookSpec.Default;
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string? TraceId { get; init; }
    public string SessionId { get; init; } = "";
    public string? MessageId { get; init; }
    public int? Step { get; init; }
    public IReadOnlyDictionary<string, object?> Input { get; init; } = EmptyInput;
    public IDictionary<string, object?> Mutable { get; init; } = new Dictionary<string, object?>();
    public IReadOnlyDictionary<string, object?> Result { get; init; } = EmptyResult;
    public CancellationToken CancellationToken { get; init; }

    public static HookPayload Blocking(
        HookSpec spec,
        string sessionId,
        IReadOnlyDictionary<string, object?>? input = null,
        IDictionary<string, object?>? mutable = null,
        CancellationToken cancellationToken = default)
    {
        if (spec.Policy != HookPolicy.Blocking)
            throw new ArgumentException($"Spec {spec.Point} 不是阻塞策略");

        return new HookPayload
        {
            Spec = spec,
            SessionId = sessionId,
            Input = input ?? EmptyInput,
            Mutable = mutable ?? new Dictionary<string, object?>(),
            CancellationToken = cancellationToken
        };
    }

    public static HookPayload FireAndForget(
        HookSpec spec,
        string sessionId,
        IReadOnlyDictionary<string, object?>? input = null,
        IReadOnlyDictionary<string, object?>? result = null,
        CancellationToken cancellationToken = default)
    {
        if (spec.Policy != HookPolicy.FireAndForget)
            throw new ArgumentException($"Spec {spec.Point} 不是非阻塞策略");

        return new HookPayload
        {
            Spec = spec,
            SessionId = sessionId,
            Input = input ?? EmptyInput,
            Result = result ?? EmptyResult,
            CancellationToken = cancellationToken
        };
    }

    public static HookPayload Parallel(
        HookSpec spec,
        string sessionId,
        IReadOnlyDictionary<string, object?>? input = null,
        CancellationToken cancellationToken = default)
    {
        if (spec.Policy != HookPolicy.Parallel)
            throw new ArgumentException($"Spec {spec.Point} 不是并行策略");

        return new HookPayload
        {
            Spec = spec,
            SessionId = sessionId,
            Input = input ?? EmptyInput,
            CancellationToken = cancellationToken
        };
    }

    public T? GetInput<T>(string key) =>
        Input.TryGetValue(key, out var value) && value is T typed ? typed : default;

    public T? GetResult<T>(string key) =>
        Result.TryGetValue(key, out var value) && value is T typed ? typed : default;

    public void SetMutable<T>(string key, T? value) => Mutable[key] = value;
}