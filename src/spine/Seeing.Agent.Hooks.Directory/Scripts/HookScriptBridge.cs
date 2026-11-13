using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Hooks.Directory.Projection;

namespace Seeing.Agent.Hooks.Directory.Scripts;

public sealed class HookScriptBridge : IHookHandler
{
    private readonly string _scriptPath;
    private readonly INodeHookInvoker _invoker;
    private readonly ILogger _logger;

    public HookScriptBridge(
        string scriptPath,
        HookSpec spec,
        int priority,
        INodeHookInvoker invoker,
        ILogger logger)
    {
        _scriptPath = scriptPath;
        Spec = HookPointNames.Resolve(spec.Point);
        Priority = priority;
        _invoker = invoker;
        _logger = logger;
    }

    public HookSpec Spec { get; }

    public int Priority { get; }

    public async Task<HookResult> ExecuteAsync(HookPayload payload)
    {
        try
        {
            return await ExecuteCoreAsync(payload).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 含 OperationCanceledException。冒泡会被 HookManager 收成 Continue: false。
            Warn(payload, "脚本桥失败", ex);
            return HookResult.Success;
        }
    }

    private async Task<HookResult> ExecuteCoreAsync(HookPayload payload)
    {
        JsonElement evt;
        try
        {
            evt = BuildEvent(payload);
        }
        catch (Exception ex)
        {
            Warn(payload, "投影失败", ex);
            return HookResult.Success;
        }

        ScriptInvocationResult result;
        try
        {
            result = await _invoker
                .InvokeAsync(_scriptPath, evt, payload.CancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Warn(payload, "调用失败", ex);
            return HookResult.Success;
        }

        return Apply(payload, result);
    }

    private HookResult Apply(HookPayload payload, ScriptInvocationResult result)
    {
        switch (result.Status)
        {
            case ScriptCallStatus.Canceled:
                return HookResult.Success;
            case ScriptCallStatus.Failed:
                Warn(payload, result.Error ?? "脚本失败", null);
                return HookResult.Success;
            case ScriptCallStatus.Succeeded:
                return ApplySucceeded(payload, result);
            default:
                Warn(payload, "未知调用状态", null);
                return HookResult.Success;
        }
    }

    private HookResult ApplySucceeded(HookPayload payload, ScriptInvocationResult result)
    {
        if (result.Mutable is not JsonElement mutable
            || mutable.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return result.Continue ? HookResult.Success : HookResult.Stop;
        }

        if (mutable.ValueKind != JsonValueKind.Object)
        {
            Warn(payload, "mutable 不是 JSON 对象", null);
            return HookResult.Success;
        }

        try
        {
            if (!HookPayloadProjection.TryWrite(payload.Mutable, mutable, out var error))
            {
                Warn(payload, error ?? "写回失败", null);
                return HookResult.Success;
            }
        }
        catch (Exception ex)
        {
            Warn(payload, "写回失败", ex);
            return HookResult.Success;
        }

        return result.Continue ? HookResult.Success : HookResult.Stop;
    }

    private JsonElement BuildEvent(HookPayload payload)
    {
        var evt = new JsonObject
        {
            ["point"] = payload.Spec.Point,
            ["sessionId"] = payload.SessionId ?? "",
            ["messageId"] = payload.MessageId,
            ["step"] = payload.Step is int step ? JsonValue.Create(step) : null,
            ["input"] = HookPayloadProjection.Project(payload.Input, NoteDropped),
            ["mutable"] = HookPayloadProjection.Project(ReadMutable(payload.Mutable), NoteDropped),
            ["result"] = HookPayloadProjection.Project(payload.Result, NoteDropped),
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(evt, HookJson.Options);
        using var document = JsonDocument.Parse(bytes);
        return document.RootElement.Clone();
    }

    private static IReadOnlyDictionary<string, object?> ReadMutable(IDictionary<string, object?> bag)
    {
        if (bag is IReadOnlyDictionary<string, object?> readOnly)
            return readOnly;

        var copy = new Dictionary<string, object?>(bag.Count);
        foreach (var pair in bag)
            copy.Add(pair.Key, pair.Value);
        return copy;
    }

    private void NoteDropped(string key)
    {
        try
        {
            _logger.LogDebug("脚本钩子投影丢弃不可序列化的键 {Key}。脚本 {ScriptPath}", key, _scriptPath);
        }
        catch
        {
            // 丢键日志失败仍继续投影，不把它变成整次放行。
        }
    }

    private void Warn(HookPayload? payload, string detail, Exception? exception)
    {
        try
        {
            _logger.LogWarning(
                exception,
                "脚本钩子失败已放行。脚本 {ScriptPath}，钩子点 {Point}。{Detail}",
                _scriptPath,
                payload?.Spec.Point ?? "",
                detail);
        }
        catch
        {
            // 日志失败也不能冒泡给 HookManager。
        }
    }
}
