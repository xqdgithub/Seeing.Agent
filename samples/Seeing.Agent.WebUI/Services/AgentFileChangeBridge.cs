using System.Collections.Concurrent;
using System.Text.Json;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Files;

namespace Seeing.Agent.WebUI.Services;

/// <summary>会话事件 → 文件变更的宿主桥接（circuit 维度 IStreamConsumer）。</summary>
public sealed class AgentFileChangeBridge : IStreamConsumer, IDisposable
{
    private static readonly HashSet<string> FileTools = new(StringComparer.Ordinal)
    {
        "write", "edit", "delete"
    };

    private readonly IFileWorkspaceFactory _factory;
    private readonly ConcurrentDictionary<string, IFileWorkspace> _workspaces = new(StringComparer.Ordinal);
    private readonly object _resolveLock = new();
    private string? _bound;

    /// <summary>agent 改动文件事件（id + 可选 diff）。</summary>
    public event Func<FileResourceId, string?, Task>? AgentChanged;

    /// <summary>注入工厂。</summary>
    public AgentFileChangeBridge(IFileWorkspaceFactory factory) => _factory = factory;

    /// <inheritdoc />
    public string SessionId => _bound ?? string.Empty;

    /// <summary>绑定父会话。</summary>
    public void Rebind(string sessionId) => _bound = sessionId;

    /// <inheritdoc />
    public void OnEvent(IMessageEvent evt)
    {
        if (evt is not ToolCallEvent tc) return;
        if (tc.Status != ToolCallStatus.Success) return;
        if (!FileTools.Contains(tc.ToolName)) return;

        var path = ExtractPath(tc);
        if (string.IsNullOrEmpty(path)) return;

        var diff = tc.Metadata is not null && tc.Metadata.TryGetValue("diff", out var d) ? d?.ToString() : null;
        _ = DispatchAsync(tc.SessionId, path, diff);
    }

    private static string? ExtractPath(ToolCallEvent tc)
    {
        if (tc.Metadata is not null && tc.Metadata.TryGetValue("path", out var p) && p is not null)
            return p.ToString();
        if (tc.Arguments is JsonElement je && je.ValueKind == JsonValueKind.Object
            && je.TryGetProperty("path", out var argPath)
            && argPath.ValueKind == JsonValueKind.String)
            return argPath.GetString();
        return null;
    }

    private async Task DispatchAsync(string sessionId, string path, string? diff)
    {
        var handler = AgentChanged;
        if (handler is null) return;
        try
        {
            var ws = await GetWorkspaceAsync(sessionId).ConfigureAwait(false);
            var id = ws.GetResourceId(path);
            await handler(id, diff).ConfigureAwait(false);
        }
        catch
        {
            // 桥接失败不致命：不中断事件广播
        }
    }

    // 每个会话只 Resolve 一次并缓存，避免每事件自增引用计数（circuit 结束时统一 Release）
    private async Task<IFileWorkspace> GetWorkspaceAsync(string sessionId)
    {
        if (_workspaces.TryGetValue(sessionId, out var cached))
            return cached;

        var ws = await _factory.ResolveAsync(sessionId).ConfigureAwait(false);
        lock (_resolveLock)
        {
            if (_workspaces.TryGetValue(sessionId, out var race))
            {
                // 竞争：保留既有实例并释放本次多出的引用
                _factory.Release(sessionId);
                return race;
            }
            _workspaces[sessionId] = ws;
            return ws;
        }
    }

    /// <inheritdoc />
    public void OnStreamEnd() { }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var sessionId in _workspaces.Keys)
            _factory.Release(sessionId);
        _workspaces.Clear();
    }
}
