using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;

namespace Seeing.Agent.Tui.Services;

/// <summary>
/// 在终端确认权限
/// </summary>
public sealed class ConsolePermissionChannel : IPermissionChannel
{
    private Func<PermissionRequest, Task<bool>>? _handler;

    /// <inheritdoc />
    public Task<bool> RequestConfirmationAsync(PermissionRequest request)
    {
        if (_handler != null)
            return _handler(request);

        var patterns = string.Join(", ", request.Patterns);
        Console.WriteLine($"权限请求: {request.Permission} — {patterns}");
        Console.Write("是否允许？(y/N): ");
        var response = Console.ReadLine();
        return Task.FromResult(response?.ToLower() == "y");
    }

    /// <inheritdoc />
    public Task<PermissionDecision> RequestToolPermissionAsync(
        string toolName,
        object? arguments,
        AgentContext context)
    {
        Console.WriteLine($"工具调用权限请求: {toolName}");
        Console.Write("是否允许执行？(y/N): ");
        var response = Console.ReadLine();
        return Task.FromResult(response?.ToLower() == "y"
            ? PermissionDecision.Allow()
            : PermissionDecision.Deny("用户拒绝"));
    }

    /// <inheritdoc />
    public Task<PermissionDecision> RequestSubAgentPermissionAsync(
        string agentName,
        string prompt,
        AgentContext context)
    {
        var promptPreview = prompt.Length > 100 ? prompt.Substring(0, 100) + "..." : prompt;
        Console.WriteLine($"子代理调用权限请求: {agentName}");
        Console.WriteLine($"提示词: {promptPreview}");
        Console.Write("是否允许执行？(y/N): ");
        var response = Console.ReadLine();
        return Task.FromResult(response?.ToLower() == "y"
            ? PermissionDecision.Allow()
            : PermissionDecision.Deny("用户拒绝"));
    }

    /// <inheritdoc />
    public Task<PermissionDecision> RequestWritePermissionAsync(
        string filePath,
        string? contentPreview,
        AgentContext context)
    {
        Console.WriteLine($"文件写入权限请求: {filePath}");
        Console.Write("是否允许写入？(y/N): ");
        var response = Console.ReadLine();
        return Task.FromResult(response?.ToLower() == "y"
            ? PermissionDecision.Allow()
            : PermissionDecision.Deny("用户拒绝"));
    }

    /// <inheritdoc />
    public void SetConfirmationHandler(Func<PermissionRequest, Task<bool>> handler) =>
        _handler = handler;
}