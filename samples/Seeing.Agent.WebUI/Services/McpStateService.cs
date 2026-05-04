namespace Seeing.Agent.WebUI.Services;

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Seeing.Agent.MCP.Core;

/// <summary>
/// MCP 状态服务 - Blazor UI 状态同步
/// </summary>
public sealed class McpStateService : IDisposable
{
    private readonly IMcpManager _manager;
    private readonly ILogger<McpStateService> _logger;
    
    private readonly ConcurrentDictionary<string, McpServerStatus> _cache = new();
    private Timer? _refreshTimer;
    
    /// <summary>状态变更事件（UI 组件订阅）</summary>
    public event EventHandler? StateChanged;
    
    public McpStateService(IMcpManager manager, ILogger<McpStateService> logger)
    {
        _manager = manager;
        _logger = logger;
        
        _manager.StatusChanged += OnManagerStatusChanged;
        
        // 定时刷新（1秒）
        _refreshTimer = new Timer(
            RefreshCache, 
            null, 
            TimeSpan.FromSeconds(1), 
            TimeSpan.FromSeconds(1));
        
        _logger.LogInformation("McpStateService 已初始化");
    }
    
    /// <summary>获取缓存的所有状态</summary>
    public IReadOnlyDictionary<string, McpServerStatus> GetCachedStatus() => _cache;
    
    /// <summary>获取指定 Server 的缓存状态</summary>
    public McpServerStatus? GetCachedStatus(string serverName)
        => _cache.TryGetValue(serverName, out var status) ? status : null;
    
    /// <summary>获取可用 Server 数量</summary>
    public int GetAvailableCount()
        => _cache.Values.Count(s => s.State == McpConnectionState.Connected);
    
    /// <summary>获取总工具数量</summary>
    public int GetTotalToolCount()
        => _cache.Values.Sum(s => s.ToolCount);
    
    private void RefreshCache(object? state)
    {
        try
        {
            var allStatus = _manager.GetAllStatus();
            foreach (var kvp in allStatus)
            {
                _cache[kvp.Key] = kvp.Value.Clone();
            }
            
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "刷新 MCP 状态缓存失败");
        }
    }
    
    private void OnManagerStatusChanged(object? sender, McpStatusChangedEventArgs e)
    {
        _cache[e.ServerName] = e.Status.Clone();
        StateChanged?.Invoke(this, EventArgs.Empty);
        
        _logger.LogDebug("MCP 状态变更: {Server} -> {State}", e.ServerName, e.NewState);
    }
    
    /// <summary>手动刷新</summary>
    public void ManualRefresh()
    {
        RefreshCache(null);
    }
    
    public void Dispose()
    {
        _manager.StatusChanged -= OnManagerStatusChanged;  // 防止内存泄漏
        _refreshTimer?.Dispose();
        
        _logger.LogInformation("McpStateService 已释放");
    }
}