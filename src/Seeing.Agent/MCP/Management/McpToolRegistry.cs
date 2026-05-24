namespace Seeing.Agent.MCP.Management;

using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.MCP;
using Seeing.Agent.MCP.Core;
using Seeing.Agent.Tools;
using System.Collections.Concurrent;

internal sealed class McpToolRegistry : IMcpToolRegistry
{
    private readonly ToolInvoker _toolInvoker;
    private readonly IHookManager _hookManager;
    private readonly ILogger _logger;

    private readonly ConcurrentDictionary<string, HashSet<string>> _serverTools = new();
    private readonly ConcurrentDictionary<string, McpTool> _mcpTools = new();

    public McpToolRegistry(ToolInvoker toolInvoker, IHookManager hookManager, ILogger logger)
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

        try
        {
            var toolNames = _serverTools.GetOrAdd(serverName, _ => new HashSet<string>());

            var mcpTool = new McpTool(
                serverName,
                toolInfo.Name,
                toolInfo.Description ?? string.Empty,
                toolInfo.ParametersSchema,
                (name, args) => Task.FromResult(new McpToolResult { IsError = true, Content = "工具执行器未设置" }));

            _mcpTools[toolId] = mcpTool;
            toolNames.Add(toolId);

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

        _logger.LogInformation("注销 MCP Server {Server} 的 {Count} 个工具", serverName, removedCount);

        return McpOperationResult.Succeeded(serverName, McpOperationType.Remove, null);
    }

    public void UpdateToolExecutor(string serverName, Func<string, Dictionary<string, object?>, Task<McpToolResult>> executor)
    {
        if (!_serverTools.TryGetValue(serverName, out var toolIds))
            return;

        foreach (var toolId in toolIds)
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
                _toolInvoker.RegisterTool(newTool);
            }
        }
    }

    public bool ContainsTool(string toolId) => _mcpTools.ContainsKey(toolId);

    public bool HasTool(string toolId) => _mcpTools.ContainsKey(toolId);

    public int GetToolCount(string serverName)
    {
        return _serverTools.TryGetValue(serverName, out var tools) ? tools.Count : 0;
    }
}