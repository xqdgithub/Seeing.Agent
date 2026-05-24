using Microsoft.Extensions.Logging;
using Seeing.Session.Core;
using System.Collections.Concurrent;

namespace Seeing.Session.Hooks;

public interface ISessionHook
{
    string HookPoint { get; }
    int Priority { get; }
    Task<SessionHookResult> ExecuteAsync(SessionHookContext context);
}

public class SessionHookContext
{
    public string HookPoint { get; init; } = "";
    public string SessionId { get; init; } = "";
    public SessionData? Session { get; init; }
    public IReadOnlyDictionary<string, object?> Input { get; init; } = new Dictionary<string, object?>();
    public IReadOnlyDictionary<string, object?> Result { get; init; } = new Dictionary<string, object?>();
}

public sealed class SessionHookManager : IHookManager
{
    private readonly ConcurrentDictionary<string, List<ISessionHook>> _hooks = new();
    private readonly ILogger<SessionHookManager>? _logger;

    public SessionHookManager(ILogger<SessionHookManager>? logger = null) => _logger = logger;

    public void AddHook(ISessionHook hook)
    {
        if (hook == null) { _logger?.LogWarning("尝试注册空 Hook"); return; }
        var list = _hooks.GetOrAdd(hook.HookPoint, _ => new List<ISessionHook>());
        lock (list)
        {
            list.Add(hook);
            list.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        }
        _logger?.LogDebug("注册 Hook: {Point}, Priority={Priority}", hook.HookPoint, hook.Priority);
    }

    public bool RemoveHook(ISessionHook hook)
    {
        if (hook == null) return false;
        if (!_hooks.TryGetValue(hook.HookPoint, out var list)) return false;
        lock (list) return list.Remove(hook);
    }

    public bool ClearHooks(string hookPoint)
    {
        var removed = _hooks.TryRemove(hookPoint, out _);
        if (removed) _logger?.LogDebug("清除 Hook 点: {Point}", hookPoint);
        return removed;
    }

    public int GetHookCount(string hookPoint) => _hooks.TryGetValue(hookPoint, out var list) ? list.Count : 0;

    public async Task TriggerAsync(string hookPoint, string sessionId = "", SessionData? session = null)
    {
        if (!_hooks.TryGetValue(hookPoint, out var hooks) || hooks.Count == 0) return;

        var context = new SessionHookContext
        {
            HookPoint = hookPoint,
            SessionId = sessionId,
            Session = session
        };

        foreach (var hook in hooks)
        {
            try { await hook.ExecuteAsync(context); }
            catch (Exception ex) { _logger?.LogError(ex, "Hook 失败: {Point}", hookPoint); }
        }
    }

    public async Task TriggerAsync(string hookPoint, SessionData session)
    {
        await TriggerAsync(hookPoint, session.Id, session);
    }

    public void TriggerFireAndForget(
        string hookPoint,
        string sessionId,
        IReadOnlyDictionary<string, object?>? input = null,
        IReadOnlyDictionary<string, object?>? result = null)
    {
        if (!_hooks.TryGetValue(hookPoint, out var hooks) || hooks.Count == 0) return;

        _ = Task.Run(async () =>
        {
            var context = new SessionHookContext
            {
                HookPoint = hookPoint,
                SessionId = sessionId,
                Input = input ?? new Dictionary<string, object?>(),
                Result = result ?? new Dictionary<string, object?>()
            };

            foreach (var hook in hooks)
            {
                try { await hook.ExecuteAsync(context); }
                catch (Exception ex) { _logger?.LogError(ex, "Hook 失败: {Point}", hookPoint); }
            }
        });
    }
}