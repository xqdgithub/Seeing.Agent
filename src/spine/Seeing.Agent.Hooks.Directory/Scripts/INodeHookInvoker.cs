using System.Text.Json;

namespace Seeing.Agent.Hooks.Directory.Scripts;

public enum ScriptCallStatus
{
    Succeeded,
    Failed,
    Canceled,
}

public record ScriptInvocationResult(ScriptCallStatus Status, bool Continue, JsonElement? Mutable, string? Error)
{
    public static ScriptInvocationResult Failure(string error) =>
        new(ScriptCallStatus.Failed, Continue: true, Mutable: null, Error: error);

    public static ScriptInvocationResult Cancel() =>
        new(ScriptCallStatus.Canceled, Continue: true, Mutable: null, Error: null);
}

public interface INodeHookInvoker
{
    bool EnsureAvailable();

    Task<ScriptInvocationResult> InvokeAsync(
        string scriptPath,
        JsonElement eventPayload,
        CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}
