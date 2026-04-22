using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;

namespace Seeing.Agent.Tui.Services;

/// <summary>
/// 权限请求事件参数 - 用于非阻塞权限确认
/// </summary>
public class PermissionRequestedEventArgs : EventArgs
{
    /// <summary>请求类型（tool/subagent/write）</summary>
    public PermissionRequestType RequestType { get; }
    
    /// <summary>请求 ID（用于追踪）</summary>
    public string RequestId { get; } = Guid.NewGuid().ToString("N");
    
    /// <summary>权限请求详情</summary>
    public PermissionRequest? Request { get; init; }
    
    /// <summary>扩展权限请求详情</summary>
    public ExtendedPermissionRequest? ExtendedRequest { get; init; }
    
    /// <summary>工具名称（仅 Tool 类型）</summary>
    public string? ToolName { get; init; }
    
    /// <summary>工具参数（仅 Tool 类型）</summary>
    public object? ToolArguments { get; init; }
    
    /// <summary>子代理名称（仅 SubAgent 类型）</summary>
    public string? SubAgentName { get; init; }
    
    /// <summary>提示词预览（仅 SubAgent 类型）</summary>
    public string? PromptPreview { get; init; }
    
    /// <summary>文件路径（仅 Write 类型）</summary>
    public string? FilePath { get; init; }
    
    /// <summary>内容预览（仅 Write 类型）</summary>
    public string? ContentPreview { get; init; }
    
    /// <summary>执行上下文</summary>
    public AgentContext? Context { get; init; }
    
    /// <summary>请求时间戳</summary>
    public DateTimeOffset RequestedAt { get; } = DateTimeOffset.UtcNow;
    
    /// <summary>超时时间（秒）</summary>
    public int TimeoutSeconds { get; }
    
    /// <summary>响应来源（TaskCompletionSource）</summary>
    private readonly TaskCompletionSource<PermissionResponse> _responseSource;
    
    /// <summary>是否已响应</summary>
    public bool HasResponded => _responseSource.Task.IsCompleted;
    
    /// <summary>等待响应的 Task</summary>
    public Task<PermissionResponse> ResponseTask => _responseSource.Task;

    /// <summary>
    /// 创建权限请求事件参数
    /// </summary>
    private PermissionRequestedEventArgs(
        PermissionRequestType requestType,
        TaskCompletionSource<PermissionResponse> responseSource,
        int timeoutSeconds = 60)
    {
        RequestType = requestType;
        _responseSource = responseSource;
        TimeoutSeconds = timeoutSeconds;
    }

    /// <summary>创建基础权限请求</summary>
    public static PermissionRequestedEventArgs CreateForConfirmation(
        PermissionRequest request,
        TaskCompletionSource<PermissionResponse> responseSource,
        int timeoutSeconds = 60)
    {
        return new PermissionRequestedEventArgs(
            PermissionRequestType.Confirmation,
            responseSource,
            timeoutSeconds)
        {
            Request = request
        };
    }

    /// <summary>创建工具调用权限请求</summary>
    public static PermissionRequestedEventArgs CreateForTool(
        string toolName,
        object? arguments,
        AgentContext? context,
        TaskCompletionSource<PermissionResponse> responseSource,
        int timeoutSeconds = 60)
    {
        return new PermissionRequestedEventArgs(
            PermissionRequestType.Tool,
            responseSource,
            timeoutSeconds)
        {
            ToolName = toolName,
            ToolArguments = arguments,
            Context = context
        };
    }

    /// <summary>创建子代理调用权限请求</summary>
    public static PermissionRequestedEventArgs CreateForSubAgent(
        string agentName,
        string prompt,
        AgentContext? context,
        TaskCompletionSource<PermissionResponse> responseSource,
        int timeoutSeconds = 60)
    {
        // 截取提示词预览（最大 100 字符）
        var preview = prompt.Length > 100 ? prompt.Substring(0, 100) + "..." : prompt;
        
        return new PermissionRequestedEventArgs(
            PermissionRequestType.SubAgent,
            responseSource,
            timeoutSeconds)
        {
            SubAgentName = agentName,
            PromptPreview = preview,
            Context = context
        };
    }

    /// <summary>创建文件写入权限请求</summary>
    public static PermissionRequestedEventArgs CreateForWrite(
        string filePath,
        string? contentPreview,
        AgentContext? context,
        TaskCompletionSource<PermissionResponse> responseSource,
        int timeoutSeconds = 60)
    {
        return new PermissionRequestedEventArgs(
            PermissionRequestType.FileWrite,
            responseSource,
            timeoutSeconds)
        {
            FilePath = filePath,
            ContentPreview = contentPreview,
            Context = context
        };
    }

    /// <summary>响应允许</summary>
    public void Allow(string? reason = null)
    {
        _responseSource.TrySetResult(PermissionResponse.Allow(reason));
    }

    /// <summary>响应拒绝</summary>
    public void Deny(string? reason = null)
    {
        _responseSource.TrySetResult(PermissionResponse.Deny(reason ?? "用户拒绝"));
    }

    /// <summary>响应超时</summary>
    public void Timeout()
    {
        _responseSource.TrySetResult(PermissionResponse.Timeout());
    }

    /// <summary>获取显示标题（用于 UI）</summary>
    public string GetDisplayTitle()
    {
        return RequestType switch
        {
            PermissionRequestType.Confirmation => $"权限确认: {Request?.Permission ?? "未知"}",
            PermissionRequestType.Tool => $"工具调用: {ToolName}",
            PermissionRequestType.SubAgent => $"子代理: {SubAgentName}",
            PermissionRequestType.FileWrite => $"文件写入: {FilePath}",
            _ => "权限请求"
        };
    }

    /// <summary>获取显示详情（用于 UI）</summary>
    public string GetDisplayDetails()
    {
        return RequestType switch
        {
            PermissionRequestType.Confirmation => 
                $"权限: {Request?.Permission}\n模式: {string.Join(", ", Request?.Patterns ?? new List<string>())}",
            PermissionRequestType.Tool => 
                $"工具: {ToolName}\n参数: {ToolArguments?.ToString() ?? "无"}",
            PermissionRequestType.SubAgent => 
                $"代理: {SubAgentName}\n提示词预览:\n{PromptPreview}",
            PermissionRequestType.FileWrite => 
                $"文件: {FilePath}\n内容预览:\n{(ContentPreview?.Length > 200 ? ContentPreview.Substring(0, 200) + "..." : ContentPreview ?? "无")}",
            _ => "无详情"
        };
    }
}

/// <summary>权限请求类型</summary>
public enum PermissionRequestType
{
    /// <summary>基础确认</summary>
    Confirmation,
    /// <summary>工具调用</summary>
    Tool,
    /// <summary>子代理调用</summary>
    SubAgent,
    /// <summary>文件写入</summary>
    FileWrite
}

/// <summary>权限响应结果</summary>
public record PermissionResponse
{
    /// <summary>是否允许</summary>
    public bool IsAllowed { get; init; }
    
    /// <summary>是否超时</summary>
    public bool IsTimeout { get; init; }
    
    /// <summary>原因</summary>
    public string? Reason { get; init; }

    /// <summary>创建允许响应</summary>
    public static PermissionResponse Allow(string? reason = null) 
        => new() { IsAllowed = true, Reason = reason };
    
    /// <summary>创建拒绝响应</summary>
    public static PermissionResponse Deny(string? reason) 
        => new() { IsAllowed = false, Reason = reason };
    
    /// <summary>创建超时响应</summary>
    public static PermissionResponse Timeout() 
        => new() { IsTimeout = true, IsAllowed = false, Reason = "等待超时" };
    
    /// <summary>转换为 PermissionDecision</summary>
    public PermissionDecision ToDecision()
    {
        if (IsTimeout)
            return PermissionDecision.Deny(Reason ?? "等待超时");
        
        return IsAllowed 
            ? PermissionDecision.Allow(Reason) 
            : PermissionDecision.Deny(Reason ?? "用户拒绝");
    }
}