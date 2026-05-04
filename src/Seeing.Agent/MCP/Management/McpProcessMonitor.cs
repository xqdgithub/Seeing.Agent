namespace Seeing.Agent.MCP.Management;

using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Seeing.Agent.MCP.Core;

/// <summary>
/// MCP 进程监控器 - 监控 stdio 进程退出事件
/// </summary>
internal sealed class McpProcessMonitor
{
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, Process> _processes = new();
    private readonly ConcurrentDictionary<string, Action<string, McpErrorInfo>> _errorCallbacks = new();

    public McpProcessMonitor(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>监控进程退出</summary>
    public void Watch(string serverName, Process process, Action<string, McpErrorInfo> onError)
    {
        if (process == null || process.HasExited)
        {
            _logger.LogWarning("无法监控已退出的进程: {Server}", serverName);
            return;
        }

        _processes[serverName] = process;
        _errorCallbacks[serverName] = onError;

        process.EnableRaisingEvents = true;
        process.Exited += (s, e) => OnProcessExited(serverName);

        _logger.LogDebug("开始监控 MCP 进程: {Server}, PID={Pid}", serverName, process.Id);
    }

    /// <summary>停止监控</summary>
    public void Unwatch(string serverName)
    {
        if (_processes.TryRemove(serverName, out var process))
        {
            try
            {
                process.EnableRaisingEvents = false;
                process.Exited -= (s, e) => OnProcessExited(serverName);
            }
            catch { }
        }

        _errorCallbacks.TryRemove(serverName, out _);
    }

    /// <summary>停止所有监控</summary>
    public void Stop()
    {
        foreach (var kvp in _processes)
        {
            try
            {
                kvp.Value.EnableRaisingEvents = false;
            }
            catch { }
        }

        _processes.Clear();
        _errorCallbacks.Clear();
    }

    private void OnProcessExited(string serverName)
    {
        _logger.LogWarning("MCP 进程异常退出: {Server}", serverName);

        int? exitCode = null;
        if (_processes.TryGetValue(serverName, out var process))
        {
            try
            {
                exitCode = process.ExitCode;
            }
            catch { }
        }

        var error = McpErrorInfo.ProcessCrashed(serverName, exitCode);

        if (_errorCallbacks.TryGetValue(serverName, out var callback))
        {
            callback(serverName, error);
        }

        Unwatch(serverName);
    }

    /// <summary>获取进程信息</summary>
    public Process? GetProcess(string serverName)
    {
        return _processes.TryGetValue(serverName, out var process) ? process : null;
    }

    /// <summary>检查进程是否存活</summary>
    public bool IsProcessAlive(string serverName)
    {
        if (!_processes.TryGetValue(serverName, out var process))
        {
            return false;
        }

        try
        {
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }
}