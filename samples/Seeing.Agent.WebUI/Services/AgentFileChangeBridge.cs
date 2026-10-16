using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Files;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.WebUI.Files.Services;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Seeing.Agent.WebUI.Services;

/// <summary>会话事件 → 文件变更的宿主桥接（circuit 维度 IStreamConsumer）。</summary>
public sealed class AgentFileChangeBridge : IStreamConsumer, IDisposable
{
    private static readonly HashSet<string> FileTools = new(StringComparer.Ordinal)
    {
        "write", "edit", "delete"
    };

    private const string OpenFileToolName = "open_file";

    private readonly IFileWorkspaceFactory _factory;
    private readonly ConcurrentDictionary<string, IFileWorkspace> _workspaces = new(StringComparer.Ordinal);
    private readonly object _resolveLock = new();
    private string? _bound;

    /// <summary>agent 改动文件事件（id + 可选 diff）。</summary>
    public event Func<FileResourceId, FileDiffData?, Task>? AgentChanged;

    /// <summary>agent 请求打开文件事件（open_file 工具）；参数含可选跳转位置（1-based）。</summary>
    public event Func<FileResourceId, int?, int?, Task>? OpenRequested;

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

        if (string.Equals(tc.ToolName, OpenFileToolName, StringComparison.Ordinal))
        {
            var openPath = ExtractPath(tc);
            if (!string.IsNullOrEmpty(openPath))
                _ = DispatchOpenAsync(tc.SessionId, openPath, ExtractInt(tc, "line"), ExtractInt(tc, "column"));
            return;
        }

        if (!FileTools.Contains(tc.ToolName)) return;

        var path = ExtractPath(tc);
        if (string.IsNullOrEmpty(path)) return;

        _ = DispatchAsync(tc.SessionId, path, ExtractDiff(tc));
    }

    private static FileDiffData? ExtractDiff(ToolCallEvent tc)
    {
        if (tc.Metadata is null) return null;

        var unified = tc.Metadata.TryGetValue(FileMetadataKeys.Diff, out var d) ? d?.ToString() : null;
        var oldContent = tc.Metadata.TryGetValue(FileMetadataKeys.OldContent, out var o) ? o?.ToString() : null;
        var newContent = tc.Metadata.TryGetValue(FileMetadataKeys.NewContent, out var n) ? n?.ToString() : null;

        if (string.IsNullOrEmpty(unified) && oldContent is null && newContent is null)
            return null;

        return new FileDiffData(oldContent, newContent, Language: null, Unified: unified);
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

    private static int? ExtractInt(ToolCallEvent tc, string name)
    {
        if (tc.Metadata is not null && tc.Metadata.TryGetValue(name, out var v) && v is not null
            && int.TryParse(v.ToString(), out var mv))
            return mv;
        if (tc.Arguments is JsonElement je && je.ValueKind == JsonValueKind.Object
            && je.TryGetProperty(name, out var arg) && arg.ValueKind == JsonValueKind.Number
            && arg.TryGetInt32(out var iv))
            return iv;
        return null;
    }

    private async Task DispatchAsync(string sessionId, string path, FileDiffData? diff)
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

    private async Task DispatchOpenAsync(string sessionId, string path, int? line, int? column)
    {
        var handler = OpenRequested;
        if (handler is null) return;
        try
        {
            var ws = await GetWorkspaceAsync(sessionId).ConfigureAwait(false);
            var id = ws.GetResourceId(path);
            await handler(id, line, column).ConfigureAwait(false);
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
