using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;

namespace Seeing.Agent.Tui.Services;

/// <summary>
/// 非阻塞权限请求通道 - 使用事件驱动而非 Console.ReadLine
/// </summary>
/// <remarks>
/// 设计目标：
/// 1. 非阻塞：使用 TaskCompletionSource 等待 UI 响应，不阻塞线程
/// 2. 事件驱动：通过 PermissionRequested 事件通知 UI 有新请求
/// 3. 超时支持：可配置超时时间，避免无限等待
/// 4. 模态对话框集成：支持 Spectre.Console 模态确认对话框
/// </remarks>
public sealed class NonBlockingPermissionChannel : IPermissionChannel
{
    /// <summary>默认超时时间（秒）</summary>
    public const int DefaultTimeoutSeconds = 60;
    
    /// <summary>权限请求事件 - UI 应订阅此事件处理权限请求</summary>
    public event EventHandler<PermissionRequestedEventArgs>? PermissionRequested;
    
    /// <summary>权限请求超时事件</summary>
    public event EventHandler<PermissionRequestedEventArgs>? PermissionTimeout;
    
    /// <summary>超时时间（秒）</summary>
    public int TimeoutSeconds { get; set; } = DefaultTimeoutSeconds;
    
    /// <summary>是否启用自动超时拒绝</summary>
    public bool AutoTimeoutDeny { get; set; } = true;
    
    /// <summary>待处理的请求计数</summary>
    private int _pendingCount;
    
    /// <summary>获取当前待处理请求数量</summary>
    public int PendingRequestCount => _pendingCount;

    /// <inheritdoc />
    public async Task<bool> RequestConfirmationAsync(PermissionRequest request)
    {
        var responseSource = new TaskCompletionSource<PermissionResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        
        var args = PermissionRequestedEventArgs.CreateForConfirmation(
            request, responseSource, TimeoutSeconds);
        
        // 发送事件通知 UI
        OnPermissionRequested(args);
        
        // 等待响应或超时
        return await WaitForResponseAsync(responseSource, args, 
            result => result.IsAllowed);
    }

    /// <inheritdoc />
    public async Task<PermissionDecision> RequestToolPermissionAsync(
        string toolName,
        object? arguments,
        AgentContext context)
    {
        var responseSource = new TaskCompletionSource<PermissionResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        
        var args = PermissionRequestedEventArgs.CreateForTool(
            toolName, arguments, context, responseSource, TimeoutSeconds);
        
        OnPermissionRequested(args);
        
        return await WaitForResponseAsync(responseSource, args, 
            result => result.ToDecision());
    }

    /// <inheritdoc />
    public async Task<PermissionDecision> RequestSubAgentPermissionAsync(
        string agentName,
        string prompt,
        AgentContext context)
    {
        var responseSource = new TaskCompletionSource<PermissionResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        
        var args = PermissionRequestedEventArgs.CreateForSubAgent(
            agentName, prompt, context, responseSource, TimeoutSeconds);
        
        OnPermissionRequested(args);
        
        return await WaitForResponseAsync(responseSource, args, 
            result => result.ToDecision());
    }

    /// <inheritdoc />
    public async Task<PermissionDecision> RequestWritePermissionAsync(
        string filePath,
        string? contentPreview,
        AgentContext context)
    {
        var responseSource = new TaskCompletionSource<PermissionResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        
        var args = PermissionRequestedEventArgs.CreateForWrite(
            filePath, contentPreview, context, responseSource, TimeoutSeconds);
        
        OnPermissionRequested(args);
        
        return await WaitForResponseAsync(responseSource, args, 
            result => result.ToDecision());
    }

    /// <summary>
    /// 等待响应（带超时处理）
    /// </summary>
    private async Task<TResult> WaitForResponseAsync<TResult>(
        TaskCompletionSource<PermissionResponse> responseSource,
        PermissionRequestedEventArgs args,
        Func<PermissionResponse, TResult> resultConverter)
    {
        _pendingCount++;
        
        try
        {
            // 使用超时等待
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(args.TimeoutSeconds));
            
            // 等待响应或超时
            var responseTask = responseSource.Task;
            var completedTask = await Task.WhenAny(
                responseTask, 
                Task.Delay(args.TimeoutSeconds * 1000, cts.Token));
            
            if (completedTask == responseTask)
            {
                // 收到响应
                var response = await responseTask;
                return resultConverter(response);
            }
            else
            {
                // 超时
                args.Timeout();
                OnPermissionTimeout(args);
                
                if (AutoTimeoutDeny)
                {
                    return resultConverter(PermissionResponse.Timeout());
                }
                else
                {
                    // 不自动拒绝，返回默认拒绝
                    return resultConverter(PermissionResponse.Deny("等待超时，默认拒绝"));
                }
            }
        }
        finally
        {
            _pendingCount--;
        }
    }

    /// <summary>触发权限请求事件</summary>
    private void OnPermissionRequested(PermissionRequestedEventArgs args)
    {
        PermissionRequested?.Invoke(this, args);
    }

    /// <summary>触发权限超时事件</summary>
    private void OnPermissionTimeout(PermissionRequestedEventArgs args)
    {
        PermissionTimeout?.Invoke(this, args);
    }

    /// <inheritdoc />
    public void SetConfirmationHandler(Func<PermissionRequest, Task<bool>>? handler)
    {
        // 非阻塞通道不支持直接设置 handler
        // 请订阅 PermissionRequested 事件来处理权限请求
        if (handler != null)
        {
            throw new NotSupportedException(
                "NonBlockingPermissionChannel 不支持 SetConfirmationHandler。" +
                "请订阅 PermissionRequested 事件来处理权限请求。");
        }
    }
}