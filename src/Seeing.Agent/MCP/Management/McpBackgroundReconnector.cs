namespace Seeing.Agent.MCP.Management;

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Seeing.Agent.MCP.Core;
using Seeing.Agent.MCP.Policy;
using CoreMcpConnectionState = Seeing.Agent.MCP.Core.McpConnectionState;

/// <summary>
/// MCP 后台重连器 - 定时检查错误状态的 Server 并自动重连
/// </summary>
internal sealed class McpBackgroundReconnector
{
    private readonly ILogger _logger;
    private readonly McpGlobalPolicy _policy;
    private readonly Func<string, Task<McpOperationResult>> _reconnectFunc;
    private readonly Func<IReadOnlyDictionary<string, McpServerStatus>> _getStatusFunc;
    
    private Timer? _timer;
    private CancellationToken _cancellationToken;
    private readonly ConcurrentDictionary<string, DateTime> _lastReconnectTime = new();
    private bool _isRunning;
    
    public McpBackgroundReconnector(
        ILogger logger,
        McpGlobalPolicy policy,
        Func<string, Task<McpOperationResult>> reconnectFunc,
        Func<IReadOnlyDictionary<string, McpServerStatus>> getStatusFunc)
    {
        _logger = logger;
        _policy = policy;
        _reconnectFunc = reconnectFunc;
        _getStatusFunc = getStatusFunc;
    }
    
    /// <summary>启动后台重连检查</summary>
    public void Start(CancellationToken cancellationToken)
    {
        if (_isRunning)
        {
            _logger.LogWarning("后台重连器已在运行");
            return;
        }
        
        _cancellationToken = cancellationToken;
        _isRunning = true;
        
        _timer = new Timer(
            CheckAndReconnect,
            null,
            _policy.BackgroundCheckInterval,
            _policy.BackgroundCheckInterval);
        
        _logger.LogInformation("后台重连器已启动，检查间隔: {Interval}s", 
            _policy.BackgroundCheckInterval.TotalSeconds);
    }
    
    /// <summary>停止后台重连检查</summary>
    public void Stop()
    {
        if (!_isRunning) return;
        
        _timer?.Dispose();
        _timer = null;
        _isRunning = false;
        
        _logger.LogInformation("后台重连器已停止");
    }
    
    private async void CheckAndReconnect(object? state)
    {
        if (_cancellationToken.IsCancellationRequested || !_isRunning)
        {
            Stop();
            return;
        }
        
        try
        {
            var statuses = _getStatusFunc();
            
            var errorServers = statuses
                .Where(s => !s.Value.IsDisabled)
                .Where(s => s.Value.State == CoreMcpConnectionState.Error)
                .Where(s => CanReconnect(s.Value))
                .OrderBy(s => (int)s.Value.Priority)
                .ToList();
            
            foreach (var kvp in errorServers)
            {
                var serverName = kvp.Key;
                var status = kvp.Value;
                
                if (_cancellationToken.IsCancellationRequested) break;
                
                var now = DateTime.UtcNow;
                var lastReconnect = _lastReconnectTime.TryGetValue(serverName, out var last) 
                    ? last 
                    : DateTime.MinValue;
                
                var reconnectPolicy = _policy.DefaultReconnectionPolicy;
                var nextInterval = reconnectPolicy.CalculateInterval(status.ReconnectAttempts);
                
                if (now - lastReconnect < nextInterval)
                {
                    continue;
                }
                
                _lastReconnectTime[serverName] = now;
                
                _logger.LogInformation(
                    "自动重连 MCP Server: {Server}, Attempt {Attempt}/{Max}, Interval {Interval}s",
                    serverName, status.ReconnectAttempts + 1, reconnectPolicy.MaxAttempts, nextInterval.TotalSeconds);
                
                try
                {
                    var result = await _reconnectFunc(serverName);
                    
                    if (result.Success)
                    {
                        _logger.LogInformation("MCP Server {Server} 重连成功", serverName);
                        _lastReconnectTime.TryRemove(serverName, out _);
                    }
                    else
                    {
                        _logger.LogWarning("MCP Server {Server} 重连失败: {Error}", 
                            serverName, result.Error?.UserMessage);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "自动重连异常: {Server}", serverName);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "后台重连检查异常");
        }
    }
    
    private bool CanReconnect(McpServerStatus status)
    {
        var reconnectPolicy = _policy.DefaultReconnectionPolicy;
        return reconnectPolicy.Enabled 
            && status.ReconnectAttempts < reconnectPolicy.MaxAttempts 
            && status.State == CoreMcpConnectionState.Error;
    }
    
    /// <summary>重置 Server 的重连时间（用于立即重连）</summary>
    public void ResetReconnectTime(string serverName)
    {
        _lastReconnectTime.TryRemove(serverName, out _);
    }
}