namespace Seeing.Agent.Mcp.Management;

using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Abstractions.Mcp;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Mcp;
using System.Collections.Concurrent;

internal sealed class McpToolRegistry : IMcpToolRegistry
{
    private readonly IToolManager _toolInvoker;
    private readonly IHookManager _hookManager;
    private readonly ILogger _logger;

    private readonly ConcurrentDictionary<string, HashSet<string>> _serverTools = new();
    private readonly ConcurrentDictionary<string, McpTool> _mcpTools = new();

    // server 名 → 当前持有该名称的连接标识（协调器实例，按引用比较）。
    // 用于区分「同一连接的重连」与「两个不同连接抢同一 server 名」。
    // 所有读写都在 _serverGate 内，故用普通字典即可。
    private readonly Dictionary<string, object> _serverOwners = new();
    private readonly object _serverGate = new();

    public McpToolRegistry(IToolManager toolInvoker, IHookManager hookManager, ILogger logger)
    {
        _toolInvoker = toolInvoker;
        _hookManager = hookManager;
        _logger = logger;
    }

    public IReadOnlyCollection<McpTool> GetTools() => _mcpTools.Values.ToList();

    public IReadOnlyList<string> GetServerToolIds(string serverName)
    {
        return _serverTools.TryGetValue(serverName, out var tools)
            ? tools.ToList()
            : new List<string>();
    }

    public async Task RegisterToolAsync(
        string serverName,
        string toolId,
        McpToolInfo toolInfo,
        object owner,
        CancellationToken ct = default)
    {
        var beforeResult = await _hookManager.TriggerBlockingAsync(
            HookRegistry.McpToolBeforeRegister,
            string.Empty,
            new Dictionary<string, object?>
            {
                ["serverName"] = serverName,
                ["toolId"] = toolId,
                ["toolName"] = toolInfo.Name,
                ["description"] = toolInfo.Description
            },
            cancellationToken: ct);

        if (!beforeResult.Continue)
        {
            _logger.LogWarning("MCP 工具注册被 Hook 拒绝: {ToolId}", toolId);
            return;
        }

        var addedNew = false;
        McpTool? previousTool = null;

        try
        {
            ClaimServer(serverName, owner);

            var toolNames = _serverTools.GetOrAdd(serverName, _ => new HashSet<string>());

            var mcpTool = new McpTool(
                serverName,
                toolInfo.Name,
                toolInfo.Description ?? string.Empty,
                toolInfo.ParametersSchema,
                (name, args, ct) => Task.FromResult(new McpToolResult { IsError = true, Content = "工具执行器未设置" }));

            // 同一 server 的重连（owner 相同）允许覆盖既有 id；跨连接抢名已由 ClaimServer 拒绝。
            // 与 ToolManager 对重复 id 的覆盖语义保持一致，避免重连后注册失败。
            previousTool = _mcpTools.TryGetValue(toolId, out var existing) ? existing : null;
            _mcpTools[toolId] = mcpTool;

            addedNew = toolNames.Add(toolId);

            await _toolInvoker.RegisterToolAsync(mcpTool, ct);

            _logger.LogDebug("注册 MCP 工具: {ToolId}", toolId);

            _hookManager.TriggerFireAndForget(
                HookRegistry.McpToolAfterRegister,
                string.Empty,
                new Dictionary<string, object?>
                {
                    ["serverName"] = serverName,
                    ["toolId"] = toolId,
                    ["toolName"] = toolInfo.Name
                });
        }
        catch (Exception ex)
        {
            // 仅回滚本次真正新增的条目；抢名失败（署名不符）时不得触碰对方已注册的工具。
            // 覆盖既有 id 的重连失败时恢复原工具，避免把对方/上一代的注册一起清掉。
            if (addedNew)
            {
                if (previousTool is null)
                {
                    _mcpTools.TryRemove(toolId, out _);
                }
                else
                {
                    _mcpTools[toolId] = previousTool;
                }

                if (_serverTools.TryGetValue(serverName, out var registered)
                    && registered.Remove(toolId) && registered.Count == 0)
                {
                    _serverTools.TryRemove(serverName, out _);
                    ReleaseServerOwner(serverName);
                }
            }
            else if (previousTool is not null)
            {
                _mcpTools[toolId] = previousTool;
            }

            _logger.LogError(ex, "注册 MCP 工具失败: {ToolId}", toolId);
            throw;
        }
    }

    public async Task UnregisterToolAsync(
        string serverName,
        string toolId,
        CancellationToken ct = default)
    {
        try
        {
            _toolInvoker.UnregisterTool(toolId);
            _mcpTools.TryRemove(toolId, out _);

            if (_serverTools.TryGetValue(serverName, out var toolIds))
            {
                toolIds.Remove(toolId);
                if (toolIds.Count == 0)
                {
                    _serverTools.TryRemove(serverName, out _);
                    ReleaseServerOwner(serverName);
                }
            }

            _hookManager.TriggerFireAndForget(
                HookRegistry.McpToolUnregistered,
                string.Empty,
                new Dictionary<string, object?>
                {
                    ["serverName"] = serverName,
                    ["toolId"] = toolId
                });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "注销 MCP 工具失败: {ToolId}", toolId);
        }
    }

    public async Task<McpOperationResult> UnregisterAllToolsAsync(string serverName)
    {
        if (!_serverTools.TryGetValue(serverName, out var toolIds))
        {
            // 即使没有任何工具，也要释放 server 名占用，避免移除后同名重建被误判为重复注册
            ReleaseServerOwner(serverName);
            return McpOperationResult.NoChange(serverName, McpOperationType.Remove, McpConnectionState.Removed);
        }

        var removedCount = 0;
        foreach (var toolId in toolIds)
        {
            try
            {
                _toolInvoker.UnregisterTool(toolId);
                _mcpTools.TryRemove(toolId, out _);
                removedCount++;

                _hookManager.TriggerFireAndForget(
                    HookRegistry.McpToolUnregistered,
                    string.Empty,
                    new Dictionary<string, object?>
                    {
                        ["serverName"] = serverName,
                        ["toolId"] = toolId
                    });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "注销 MCP 工具失败: {ToolId}", toolId);
            }
        }

        _serverTools.TryRemove(serverName, out _);
        ReleaseServerOwner(serverName);

        _logger.LogInformation("注销 MCP Server {Server} 的 {Count} 个工具", serverName, removedCount);

        return McpOperationResult.Succeeded(serverName, McpOperationType.Remove, null);
    }

    public async Task UpdateToolExecutorAsync(string serverName, Func<string, Dictionary<string, object?>, CancellationToken, Task<McpToolResult>> executor)
    {
        if (!_serverTools.TryGetValue(serverName, out var toolIds))
            return;

        // 快照工具 ID，避免遍历期间集合被并发修改
        foreach (var toolId in toolIds.ToArray())
        {
            if (_mcpTools.TryGetValue(toolId, out var tool))
            {
                var newTool = new McpTool(
                    serverName,
                    tool.ToolName,
                    tool.Description,
                    tool.ParametersSchema,
                    executor);

                _mcpTools[toolId] = newTool;
                _toolInvoker.UnregisterTool(toolId);
                await _toolInvoker.RegisterToolAsync(newTool).ConfigureAwait(false);
            }
        }
    }

    public bool ContainsTool(string toolId) => _mcpTools.ContainsKey(toolId);

    public bool HasTool(string toolId) => _mcpTools.ContainsKey(toolId);

    public int GetToolCount(string serverName)
    {
        return _serverTools.TryGetValue(serverName, out var tools) ? tools.Count : 0;
    }

    /// <summary>
    /// 占用 server 名：同名已被其他连接持有时抛 <see cref="McpDuplicateServerException"/>。
    /// </summary>
    public void ClaimServer(string serverName, object owner)
    {
        lock (_serverGate)
        {
            if (_serverOwners.TryGetValue(serverName, out var existing))
            {
                if (!ReferenceEquals(existing, owner))
                    throw new McpDuplicateServerException(serverName);

                return;
            }

            _serverOwners[serverName] = owner;
        }
    }

    /// <summary>
    /// 有条件释放 server 名：仅当调用方是占用者且该 server 已无注册工具时移除。
    /// </summary>
    public void ReleaseServer(string serverName, object owner)
    {
        lock (_serverGate)
        {
            if (!_serverOwners.TryGetValue(serverName, out var current)
                || !ReferenceEquals(current, owner))
            {
                return;
            }

            if (_serverTools.ContainsKey(serverName))
                return;

            _serverOwners.Remove(serverName);
        }
    }

    /// <summary>
    /// 释放 server 名占用。调用方须先注销该 server 的全部工具，确保释放时已无人使用该名称。
    /// </summary>
    private void ReleaseServerOwner(string serverName)
    {
        lock (_serverGate)
        {
            _serverOwners.Remove(serverName);
        }
    }
}