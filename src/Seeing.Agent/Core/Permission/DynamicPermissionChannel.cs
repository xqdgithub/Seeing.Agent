using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;

namespace Seeing.Agent.Core.Permission;

/// <summary>
/// 动态权限通道 - 支持配置热重载
/// <para>
/// 每次权限请求时从 IOptionsMonitor 读取最新的 AutoApproveAll 配置，
/// 实现运行时配置变更无需重启。
/// </para>
/// </summary>
public sealed class DynamicPermissionChannel : IPermissionChannel
{
    private readonly IOptionsMonitor<SeeingAgentOptions> _optionsMonitor;
    private readonly ILogger<DynamicPermissionChannel>? _logger;

    public DynamicPermissionChannel(
        IOptionsMonitor<SeeingAgentOptions> optionsMonitor,
        ILogger<DynamicPermissionChannel>? logger = null)
    {
        _optionsMonitor = optionsMonitor;
        _logger = logger;
    }

    private bool AutoApproveAll => _optionsMonitor.CurrentValue.Permission?.AutoApproveAll ?? false;

    public Task<bool> RequestConfirmationAsync(PermissionRequest request)
    {
        if (AutoApproveAll)
        {
            _logger?.LogDebug("Auto-approved permission request");
            return Task.FromResult(true);
        }

        throw new PermissionRequiredException(
            "权限请求",
            "未配置权限确认通道。请在配置中设置 Permission:AutoApproveAll=true 以自动批准所有操作（危险），或提供 IPermissionChannel 实现。");
    }

    public Task<PermissionDecision> RequestToolPermissionAsync(
        string toolName,
        object? arguments,
        AgentContext context)
    {
        if (AutoApproveAll)
        {
            _logger?.LogDebug("Auto-approved tool: {ToolName}", toolName);
            return Task.FromResult(PermissionDecision.Allow());
        }

        throw new PermissionRequiredException(
            $"工具调用: {toolName}",
            "未配置权限确认通道。工具调用需要用户确认。");
    }

    public Task<PermissionDecision> RequestSubAgentPermissionAsync(
        string agentName,
        string prompt,
        AgentContext context)
    {
        if (AutoApproveAll)
        {
            _logger?.LogDebug("Auto-approved sub-agent: {AgentName}", agentName);
            return Task.FromResult(PermissionDecision.Allow());
        }

        throw new PermissionRequiredException(
            $"子代理调用: {agentName}",
            "未配置权限确认通道。子代理调用需要用户确认。");
    }

    public Task<PermissionDecision> RequestWritePermissionAsync(
        string filePath,
        string? contentPreview,
        AgentContext context)
    {
        if (AutoApproveAll)
        {
            _logger?.LogDebug("Auto-approved write: {FilePath}", filePath);
            return Task.FromResult(PermissionDecision.Allow());
        }

        throw new PermissionRequiredException(
            $"文件写入: {filePath}",
            "未配置权限确认通道。文件写入操作需要用户确认。");
    }
}
