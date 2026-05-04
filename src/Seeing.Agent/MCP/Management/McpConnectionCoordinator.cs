namespace Seeing.Agent.MCP.Management;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.MCP;
using Seeing.Agent.MCP.Core;
using Seeing.Agent.MCP.Factory;
using Seeing.Agent.MCP.Policy;
using CoreMcpConnectionState = Seeing.Agent.MCP.Core.McpConnectionState;

internal sealed class McpConnectionCoordinator : IDisposable
{
    private readonly string _serverName;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private IMcpClientWrapper? _client;
    private Task? _backgroundConnectTask;
    private TaskCompletionSource<bool>? _readyTcs;
    private bool _disposed;

    private readonly ILogger _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly IHookManager _hookManager;
    private readonly McpWrapperFactoryRegistry _factoryRegistry;
    private readonly IMcpToolRegistry _toolRegistry;
    private readonly McpGlobalPolicy _globalPolicy;

    private readonly Action<string, McpServerStatus> _updateStatusCallback;
    private readonly Func<string, McpServerConfig?> _getConfigFunc;
    private readonly Func<string, McpServerStatus?> _getStatusFunc;

    public McpConnectionCoordinator(
        string serverName,
        ILogger logger,
        ILoggerFactory loggerFactory,
        IHttpClientFactory? httpClientFactory,
        IHookManager hookManager,
        McpWrapperFactoryRegistry factoryRegistry,
        IMcpToolRegistry toolRegistry,
        McpGlobalPolicy globalPolicy,
        Action<string, McpServerStatus> updateStatusCallback,
        Func<string, McpServerConfig?> getConfigFunc,
        Func<string, McpServerStatus?> getStatusFunc)
    {
        _serverName = serverName;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _httpClientFactory = httpClientFactory;
        _hookManager = hookManager;
        _factoryRegistry = factoryRegistry;
        _toolRegistry = toolRegistry;
        _globalPolicy = globalPolicy;
        _updateStatusCallback = updateStatusCallback;
        _getConfigFunc = getConfigFunc;
        _getStatusFunc = getStatusFunc;
    }

    public async Task<McpOperationResult> ConnectAsync(McpServerConfig config, CancellationToken ct)
    {
        if (_disposed)
            return McpOperationResult.Failed(_serverName, McpOperationType.Connect,
                McpErrorInfo.ServerPaused(_serverName));

        await _connectLock.WaitAsync(ct);
        try
        {
            return await ExecuteConnectAsync(config, ct);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async Task<McpOperationResult> ExecuteConnectAsync(McpServerConfig config, CancellationToken ct)
    {
        var startTime = DateTime.UtcNow;
        var currentStatus = _getStatusFunc(_serverName);

        if (currentStatus == null)
        {
            var error = McpErrorInfo.ConfigInvalid(_serverName, "服务器状态未初始化");
            return McpOperationResult.Failed(_serverName, McpOperationType.Connect, error);
        }

        if (currentStatus.State == CoreMcpConnectionState.Connected)
            return McpOperationResult.NoChange(_serverName, McpOperationType.Connect, currentStatus.State);

        var beforeInput = new Dictionary<string, object?>
        {
            ["serverName"] = _serverName,
            ["config"] = config
        };
        var beforeResult = await _hookManager.TriggerBlockingAsync(
            HookRegistry.McpBeforeConnect, "", beforeInput, null, ct);

        if (!beforeResult.Continue)
        {
            var error = McpErrorInfo.ConfigInvalid(_serverName, "Hook 拒绝连接");
            UpdateStateWithError(error);
            return McpOperationResult.Failed(_serverName, McpOperationType.Connect, error);
        }

        UpdateState(CoreMcpConnectionState.Connecting);

        try
        {
            _client = _factoryRegistry.Create(config, _httpClientFactory, _loggerFactory);
            await _client.ConnectAsync(ct);

            var tools = await _client.ListToolsAsync(ct);
            var toolNames = new List<string>();

            foreach (var tool in tools)
            {
                var toolId = $"{_serverName}_{tool.Name}";
                var toolInfo = new McpToolInfo
                {
                    Name = tool.Name,
                    Description = tool.Description,
                    ParametersSchema = tool.ParametersSchema
                };
                await _toolRegistry.RegisterToolAsync(_serverName, toolId, toolInfo, ct);
                toolNames.Add(tool.Name);

                _hookManager.TriggerFireAndForget(
                    HookRegistry.McpToolAfterRegister, "",
                    new Dictionary<string, object?> { ["toolId"] = toolId, ["tool"] = tool });
            }

            // 绑定工具执行器到实际客户端
            _toolRegistry.UpdateToolExecutor(_serverName, async (toolName, args) =>
            {
                if (_client == null)
                    return new McpToolResult { IsError = true, Content = "MCP 客户端未连接" };
                return await _client.CallToolAsync(toolName, args, CancellationToken.None);
            });

            var newStatus = McpServerStatusBuilder.From(currentStatus)
                .WithConnected()
                .WithToolCount(tools.Count)
                .WithToolNames(toolNames)
                .Build();

            await UpdateStateAsync(CoreMcpConnectionState.Connected, newStatus);

            CompleteReadySource(true);

            var afterInput = new Dictionary<string, object?>
            {
                ["serverName"] = _serverName,
                ["toolCount"] = tools.Count,
                ["toolNames"] = toolNames
            };
            _hookManager.TriggerFireAndForget(HookRegistry.McpAfterConnect, "", afterInput);

            _logger.LogInformation("MCP Server {Server} 连接成功，注册 {Count} 个工具",
                _serverName, tools.Count);

            return McpOperationResult.Succeeded(_serverName, McpOperationType.Connect,
                CoreMcpConnectionState.Connected, DateTime.UtcNow - startTime);
        }
        catch (OperationCanceledException)
        {
            var error = McpErrorInfo.ConnectionTimeout(_serverName, config.ConnectionTimeout);
            UpdateStateWithError(error);
            return McpOperationResult.Failed(_serverName, McpOperationType.Connect, error,
                CoreMcpConnectionState.Error, DateTime.UtcNow - startTime);
        }
        catch (Exception ex)
        {
            var error = ClassifyError(ex, config);
            UpdateStateWithError(error);
            return McpOperationResult.Failed(_serverName, McpOperationType.Connect, error,
                CoreMcpConnectionState.Error, DateTime.UtcNow - startTime);
        }
    }

    public Task ConnectInBackgroundAsync(McpServerConfig config, CancellationToken ct)
    {
        if (_disposed)
            return Task.CompletedTask;

        InitReadySource();

        _backgroundConnectTask = Task.Run(async () =>
        {
            try
            {
                await ConnectAsync(config, ct);
            }
            catch (OperationCanceledException)
            {
                CompleteReadySource(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "后台连接 MCP Server {Server} 失败", _serverName);
                CompleteReadySource(false);
            }
        }, ct);

        return _backgroundConnectTask;
    }

    public async Task<McpOperationResult> DisconnectAsync(CancellationToken ct = default)
    {
        if (_disposed)
            return McpOperationResult.Failed(_serverName, McpOperationType.Disconnect,
                McpErrorInfo.ServerRemoved(_serverName));

        await _connectLock.WaitAsync(ct);
        try
        {
            return await ExecuteDisconnectAsync(ct);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async Task<McpOperationResult> ExecuteDisconnectAsync(CancellationToken ct)
    {
        var startTime = DateTime.UtcNow;
        var currentStatus = _getStatusFunc(_serverName);

        if (currentStatus == null || currentStatus.State == CoreMcpConnectionState.Pending)
            return McpOperationResult.NoChange(_serverName, McpOperationType.Disconnect,
                currentStatus?.State ?? CoreMcpConnectionState.Pending);

        if (_client != null)
        {
            var toolNames = currentStatus.ToolNames;
            foreach (var toolName in toolNames)
            {
                var toolId = $"{_serverName}_{toolName}";
                await _toolRegistry.UnregisterToolAsync(_serverName, toolId, ct);

                _hookManager.TriggerFireAndForget(
                    HookRegistry.McpToolUnregistered, "",
                    new Dictionary<string, object?> { ["toolId"] = toolId });
            }

            try
            {
                await _client.DisconnectAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "断开 MCP Server {Server} 连接时发生错误", _serverName);
            }

            _client = null;
        }

        var newStatus = McpServerStatusBuilder.From(currentStatus!)
            .WithState(CoreMcpConnectionState.Pending)
            .WithToolCount(0)
            .WithToolNames(new List<string>())
            .Build();

        await UpdateStateAsync(CoreMcpConnectionState.Pending, newStatus);

        _hookManager.TriggerFireAndForget(
            HookRegistry.McpDisconnected, "",
            new Dictionary<string, object?> { ["serverName"] = _serverName });

        _logger.LogInformation("MCP Server {Server} 已断开连接", _serverName);

        return McpOperationResult.Succeeded(_serverName, McpOperationType.Disconnect,
            CoreMcpConnectionState.Pending, DateTime.UtcNow - startTime);
    }

    public async Task<McpOperationResult> ReconnectAsync(CancellationToken ct = default)
    {
        if (_disposed)
            return McpOperationResult.Failed(_serverName, McpOperationType.Reconnect,
                McpErrorInfo.ServerRemoved(_serverName));

        var config = _getConfigFunc(_serverName);
        if (config == null)
        {
            var error = McpErrorInfo.ConfigMissing(_serverName);
            return McpOperationResult.Failed(_serverName, McpOperationType.Reconnect, error);
        }

        var currentStatus = _getStatusFunc(_serverName);
        if (currentStatus == null)
        {
            var error = McpErrorInfo.ConfigInvalid(_serverName, "服务器状态未初始化");
            return McpOperationResult.Failed(_serverName, McpOperationType.Reconnect, error);
        }

        var beforeInput = new Dictionary<string, object?> { ["serverName"] = _serverName };
        var beforeResult = await _hookManager.TriggerBlockingAsync(
            HookRegistry.McpBeforeReconnect, "", beforeInput, null, ct);

        if (!beforeResult.Continue)
        {
            var error = McpErrorInfo.ConfigInvalid(_serverName, "Hook 拒绝重连");
            return McpOperationResult.Failed(_serverName, McpOperationType.Reconnect, error);
        }

        var startTime = DateTime.UtcNow;

        // 增加重连计数并更新状态为 Reconnecting
        var reconnectingStatus = McpServerStatusBuilder.From(currentStatus)
            .WithState(CoreMcpConnectionState.Reconnecting)
            .IncrementReconnect()
            .Build();
        await UpdateStateAsync(CoreMcpConnectionState.Reconnecting, reconnectingStatus);

        await DisconnectAsync(ct);

        var connectResult = await ConnectAsync(config, ct);

        var afterInput = new Dictionary<string, object?>
        {
            ["serverName"] = _serverName,
            ["success"] = connectResult.Success
        };
        _hookManager.TriggerFireAndForget(HookRegistry.McpAfterReconnect, "", afterInput);

        if (connectResult.Success)
        {
            return McpOperationResult.Succeeded(_serverName, McpOperationType.Reconnect,
                connectResult.Status, DateTime.UtcNow - startTime);
        }
        else
        {
            return McpOperationResult.Failed(_serverName, McpOperationType.Reconnect,
                connectResult.Error ?? McpErrorInfo.ProcessCrashed(_serverName),
                connectResult.Status, DateTime.UtcNow - startTime);
        }
    }

    public async Task<McpOperationResult> PauseAsync(CancellationToken ct = default)
    {
        if (_disposed)
            return McpOperationResult.Failed(_serverName, McpOperationType.Pause,
                McpErrorInfo.ServerRemoved(_serverName));

        var currentStatus = _getStatusFunc(_serverName);
        if (currentStatus == null)
        {
            var error = McpErrorInfo.ConfigInvalid(_serverName, "服务器状态未初始化");
            return McpOperationResult.Failed(_serverName, McpOperationType.Pause, error);
        }

        if (currentStatus.State != CoreMcpConnectionState.Connected)
        {
            return McpOperationResult.NoChange(_serverName, McpOperationType.Pause, currentStatus.State);
        }

        var startTime = DateTime.UtcNow;

        // 先断开连接
        await ExecuteDisconnectAsync(ct);

        // 设置为 Paused 状态
        var pausedStatus = McpServerStatusBuilder.From(currentStatus)
            .WithState(CoreMcpConnectionState.Paused)
            .WithToolCount(0)
            .WithToolNames(new List<string>())
            .Build();

        await UpdateStateAsync(CoreMcpConnectionState.Paused, pausedStatus);

        _logger.LogInformation("MCP Server {Server} 已暂停", _serverName);

        return McpOperationResult.Succeeded(_serverName, McpOperationType.Pause,
            CoreMcpConnectionState.Paused, DateTime.UtcNow - startTime);
    }

    public async Task<McpOperationResult> ResumeAsync(CancellationToken ct = default)
    {
        if (_disposed)
            return McpOperationResult.Failed(_serverName, McpOperationType.Resume,
                McpErrorInfo.ServerRemoved(_serverName));

        var currentStatus = _getStatusFunc(_serverName);
        if (currentStatus == null)
        {
            var error = McpErrorInfo.ConfigInvalid(_serverName, "服务器状态未初始化");
            return McpOperationResult.Failed(_serverName, McpOperationType.Resume, error);
        }

        if (currentStatus.State != CoreMcpConnectionState.Paused)
        {
            return McpOperationResult.NoChange(_serverName, McpOperationType.Resume, currentStatus.State);
        }

        var config = _getConfigFunc(_serverName);
        if (config == null)
        {
            var error = McpErrorInfo.ConfigMissing(_serverName);
            return McpOperationResult.Failed(_serverName, McpOperationType.Resume, error);
        }

        var startTime = DateTime.UtcNow;

        // 设置为 Pending 状态
        var pendingStatus = McpServerStatusBuilder.From(currentStatus)
            .WithState(CoreMcpConnectionState.Pending)
            .Build();

        await UpdateStateAsync(CoreMcpConnectionState.Pending, pendingStatus);

        // 后台启动连接
        InitReadySource();
        _ = ConnectInBackgroundAsync(config, ct);

        _logger.LogInformation("MCP Server {Server} 已恢复，正在后台连接", _serverName);

        return McpOperationResult.Succeeded(_serverName, McpOperationType.Resume,
            CoreMcpConnectionState.Pending, DateTime.UtcNow - startTime);
    }

    public async Task<bool> WaitForReadyAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        if (_disposed)
            return false;

        var currentStatus = _getStatusFunc(_serverName);
        if (currentStatus?.State == CoreMcpConnectionState.Connected)
            return true;

        InitReadySource();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(timeout);

        try
        {
            await _readyTcs!.Task.WaitAsync(linkedCts.Token);
            return _readyTcs.Task.Result;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private void UpdateState(CoreMcpConnectionState newState, McpServerStatus? customStatus = null)
    {
        var current = _getStatusFunc(_serverName);
        if (current == null) return;

        McpStateTransitions.ValidateTransition(current.State, newState);

        var newStatus = customStatus ?? McpServerStatusBuilder.From(current)
            .WithState(newState).Build();

        _updateStatusCallback(_serverName, newStatus);

        _hookManager.TriggerFireAndForget(
            HookRegistry.McpStatusChanged, "",
            new Dictionary<string, object?>
            {
                ["serverName"] = _serverName,
                ["previousState"] = current.State,
                ["newState"] = newState
            });
    }

    private async Task UpdateStateAsync(CoreMcpConnectionState newState, McpServerStatus newStatus)
    {
        await _stateLock.WaitAsync();
        try
        {
            var current = _getStatusFunc(_serverName);
            if (current == null) return;

            McpStateTransitions.ValidateTransition(current.State, newState);

            _updateStatusCallback(_serverName, newStatus);

            _hookManager.TriggerFireAndForget(
                HookRegistry.McpStatusChanged, "",
                new Dictionary<string, object?>
                {
                    ["serverName"] = _serverName,
                    ["previousState"] = current.State,
                    ["newState"] = newState
                });
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private void UpdateStateWithError(McpErrorInfo error)
    {
        var current = _getStatusFunc(_serverName);
        if (current == null) return;

        try
        {
            McpStateTransitions.ValidateTransition(current.State, CoreMcpConnectionState.Error);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "无法转换到错误状态: {Server}", _serverName);
            return;
        }

        var newStatus = McpServerStatusBuilder.From(current)
            .WithState(CoreMcpConnectionState.Error)
            .WithError(error)
            .Build();

        _updateStatusCallback(_serverName, newStatus);

        CompleteReadySource(false);

        _hookManager.TriggerFireAndForget(
            HookRegistry.McpOnError, "",
            new Dictionary<string, object?>
            {
                ["serverName"] = _serverName,
                ["error"] = error
            });
    }

    private void InitReadySource()
    {
        if (_readyTcs == null || _readyTcs.Task.IsCompleted)
        {
            _readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private void CompleteReadySource(bool success)
    {
        if (_readyTcs != null && !_readyTcs.Task.IsCompleted)
        {
            _readyTcs.TrySetResult(success);
        }
    }

    private McpErrorInfo ClassifyError(Exception ex, McpServerConfig config)
    {
        var message = ex.Message;

        if (message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("超时", StringComparison.Ordinal))
        {
            return McpErrorInfo.ConnectionTimeout(_serverName, config.ConnectionTimeout, ex);
        }

        if (message.Contains("authentication", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("认证", StringComparison.Ordinal) ||
            message.Contains("401", StringComparison.Ordinal))
        {
            return McpErrorInfo.AuthenticationFailed(_serverName, ex.Message);
        }

        if (message.Contains("403", StringComparison.Ordinal) ||
            message.Contains("forbidden", StringComparison.OrdinalIgnoreCase))
        {
            return McpErrorInfo.SessionExpired(_serverName);
        }

        if (ex is System.Net.Http.HttpRequestException)
        {
            return McpErrorInfo.ConnectionTimeout(_serverName, config.ConnectionTimeout, ex);
        }

        if (message.Contains("command not found", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("找不到", StringComparison.Ordinal))
        {
            return McpErrorInfo.ConfigInvalid(_serverName, "命令未找到");
        }

        return McpErrorInfo.ProcessCrashed(_serverName);
    }

    public IMcpClientWrapper? GetClient() => _client;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _connectLock.Dispose();
        _stateLock.Dispose();

        CompleteReadySource(false);

        if (_client != null)
        {
            try
            {
                _client.DisconnectAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "释放 MCP Server {Server} 连接时发生错误", _serverName);
            }
            _client = null;
        }
    }
}