using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Interactions;
using System.Collections.Concurrent;

namespace Seeing.Agent.WebUI.Workbench;

/// <summary>宿主请求处理器（可编组委托）。</summary>
/// <param name="Handler">处理器（内部自行编组到 UI 同步上下文）。</param>
/// <param name="Dock">可选：该处理器所属 dock（同 scope 多实例时用于消歧）。</param>
public readonly record struct WorkbenchRequestHandler(
    Func<HostRequest, CancellationToken, Task<HostResponse>> Handler,
    string? Dock = null);

/// <summary>
/// 宿主请求处理器注册表（Singleton）：按 <b>kind 前缀</b>路由，取代「每 circuit 一个面板」的旧形状。
/// <para>
/// <b>为什么需要前缀</b>：工作台是多个并存的实例。文件工作台登记 <c>workbench.files</c>，
/// Markdown 工作台登记 <c>workbench.markdown</c>，注册表动作登记 <c>workbench.registry</c>；
/// 旧的「(session, circuit) → 单 handler」在多 dock 下必然互相顶掉。
/// </para>
/// <para>
/// <b>路由规则</b>：kind 的最长前缀胜出。同 scope 多实例时按 <c>payload.dock</c> 选择；
/// 无法消歧时交给第一个，并由调用方（<c>WorkbenchClient</c>）按 <c>Ambiguous</c> 处理。
/// </para>
/// </summary>
public sealed class WorkbenchRequestHandlerRegistry
{
    private readonly ConcurrentDictionary<RegistrationKey, List<WorkbenchRequestHandler>> _handlers = new();
    private readonly ILogger<WorkbenchRequestHandlerRegistry>? _logger;

    /// <summary>创建注册表。</summary>
    public WorkbenchRequestHandlerRegistry(ILogger<WorkbenchRequestHandlerRegistry>? logger = null)
        => _logger = logger;

    /// <summary>
    /// 登记 handler（同 key 覆盖）。
    /// </summary>
    /// <param name="sessionId">会话 id。</param>
    /// <param name="circuitId">circuit id（不同标签页互不干扰）。</param>
    /// <param name="kindScope">kind 前缀，如 <c>workbench.files</c>；空串为兜底（最低优先级）。</param>
    /// <param name="handler">处理器。</param>
    public void Register(
        string sessionId,
        string circuitId,
        string kindScope,
        WorkbenchRequestHandler handler)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(circuitId))
            return;

        var key = new RegistrationKey(sessionId, circuitId, kindScope ?? string.Empty);

        _handlers.AddOrUpdate(
            key,
            _ => [handler],
            (_, existing) =>
            {
                // 同 dock 的重复登记是「重渲染后的重新登记」：替换；不同 dock 追加（多实例并存）
                var index = existing.FindIndex(item =>
                    string.Equals(item.Dock, handler.Dock, StringComparison.Ordinal));
                if (index >= 0)
                {
                    existing[index] = handler;
                    return existing;
                }

                existing.Add(handler);
                return existing;
            });
    }

    /// <summary>注销该 (session, circuit, scope) 的全部登记（幂等）。</summary>
    public void Unregister(string sessionId, string circuitId, string kindScope)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(circuitId))
            return;

        _handlers.TryRemove(new RegistrationKey(sessionId, circuitId, kindScope ?? string.Empty), out _);
    }

    /// <summary>注销该 (session, circuit) 下的全部登记（组件 Dispose / circuit 关闭）。</summary>
    public void UnregisterAll(string sessionId, string circuitId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(circuitId))
            return;

        foreach (var key in _handlers.Keys)
        {
            if (string.Equals(key.SessionId, sessionId, StringComparison.Ordinal)
                && string.Equals(key.CircuitId, circuitId, StringComparison.Ordinal))
            {
                _handlers.TryRemove(key, out _);
            }
        }
    }

    /// <summary>是否存在任一 circuit 持有该会话的处理器（用于「无 handler 兜底」判定）。</summary>
    public bool HasAny(string sessionId)
    {
        foreach (var key in _handlers.Keys)
        {
            if (string.Equals(key.SessionId, sessionId, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 解析处理器：最长 kind 前缀胜出；同 scope 多实例按 <c>payload.dock</c> 消歧。
    /// </summary>
    /// <param name="sessionId">会话 id。</param>
    /// <param name="circuitId">circuit id。</param>
    /// <param name="request">请求。</param>
    /// <param name="handler">命中的处理器。</param>
    /// <param name="ambiguous">命中多个实例且无法按 dock 消歧。</param>
    public bool TryResolve(
        string sessionId,
        string circuitId,
        HostRequest request,
        out WorkbenchRequestHandler handler,
        out bool ambiguous)
    {
        handler = default;
        ambiguous = false;

        if (string.IsNullOrWhiteSpace(request.Kind))
            return false;

        var dock = ReadDock(request.Payload);

        // 按 scope 长度降序：最长前缀优先。空 scope（""）排最后作为兜底。
        var candidates = _handlers.Keys
            .Where(key => string.Equals(key.SessionId, sessionId, StringComparison.Ordinal)
                          && string.Equals(key.CircuitId, circuitId, StringComparison.Ordinal)
                          && ScopeMatches(request.Kind, key.KindScope))
            .OrderByDescending(key => key.KindScope.Length)
            .ToArray();

        foreach (var key in candidates)
        {
            if (!_handlers.TryGetValue(key, out var list) || list.Count == 0)
                continue;

            if (list.Count == 1)
            {
                handler = list[0];
                return true;
            }

            if (dock is not null)
            {
                var byDock = list.FirstOrDefault(item =>
                    string.Equals(item.Dock, dock, StringComparison.OrdinalIgnoreCase));
                if (byDock.Handler is not null)
                {
                    handler = byDock;
                    return true;
                }
            }

            // 多实例且未指定 dock（或指定了不存在的 dock）：交给第一个，调用方按歧义处理
            handler = list[0];
            ambiguous = true;
            _logger?.LogDebug(
                "宿主请求 scope '{Scope}' 命中 {Count} 个实例且无法按 dock 消歧：{Kind}",
                key.KindScope, list.Count, request.Kind);
            return true;
        }

        return false;
    }

    private static bool ScopeMatches(string kind, string scope)
        => scope.Length == 0
           || string.Equals(kind, scope, StringComparison.Ordinal)
           || kind.StartsWith(scope + ".", StringComparison.Ordinal);

    /// <summary>读 payload 的 <c>dock</c>（大小写不敏感；不是对象或缺失返回 null）。</summary>
    private static string? ReadDock(System.Text.Json.JsonElement? payload)
        => payload is { ValueKind: System.Text.Json.JsonValueKind.Object } obj
           && obj.TryGetProperty("dock", out var dock)
           && dock.ValueKind == System.Text.Json.JsonValueKind.String
            ? dock.GetString()
            : null;

    private readonly record struct RegistrationKey(string SessionId, string CircuitId, string KindScope);
}
