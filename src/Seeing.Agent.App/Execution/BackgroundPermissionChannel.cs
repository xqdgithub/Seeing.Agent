using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Microsoft.Extensions.Logging;

namespace Seeing.Agent.App.Execution;

/// <summary>
/// Permission channel for background execution that auto-rejects after timeout.
/// Used when execution runs independently of UI connections.
/// </summary>
public class BackgroundPermissionChannel : IPermissionChannel
{
    private readonly TimeSpan _timeout;
    private readonly ILogger? _logger;

    /// <summary>
    /// Creates a new BackgroundPermissionChannel with the specified timeout.
    /// </summary>
    /// <param name="timeout">Time to wait before auto-rejecting. Default is 30 seconds.</param>
    /// <param name="logger">Optional logger.</param>
    public BackgroundPermissionChannel(TimeSpan timeout, ILogger? logger = null)
    {
        _timeout = timeout;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<bool> RequestConfirmationAsync(PermissionRequest request)
    {
        _logger?.LogWarning(
            "Background execution: Permission request '{Permission}' will be auto-rejected after {Timeout}s if not responded",
            request.Permission,
            _timeout.TotalSeconds);

        // Wait for timeout, then auto-reject
        await Task.Delay(_timeout);

        _logger?.LogWarning(
            "Background execution: Permission request '{Permission}' auto-rejected (timeout: {Timeout}s)",
            request.Permission,
            _timeout.TotalSeconds);

        return false;
    }

    /// <inheritdoc/>
    public async Task<PermissionDecision> RequestToolPermissionAsync(
        string toolName,
        object? arguments,
        AgentContext context)
    {
        _logger?.LogWarning(
            "Background execution: Tool permission request for '{Tool}' will be auto-rejected after {Timeout}s",
            toolName,
            _timeout.TotalSeconds);

        // Wait for timeout, then auto-reject
        await Task.Delay(_timeout);

        _logger?.LogWarning(
            "Background execution: Tool permission for '{Tool}' auto-rejected (timeout: {Timeout}s)",
            toolName,
            _timeout.TotalSeconds);

        return PermissionDecision.Deny(
            $"Background execution mode: Permission request timed out after {_timeout.TotalSeconds} seconds");
    }

    /// <inheritdoc/>
    public async Task<PermissionDecision> RequestSubAgentPermissionAsync(
        string agentName,
        string prompt,
        AgentContext context)
    {
        _logger?.LogWarning(
            "Background execution: Sub-agent permission request for '{Agent}' will be auto-rejected after {Timeout}s",
            agentName,
            _timeout.TotalSeconds);

        // Wait for timeout, then auto-reject
        await Task.Delay(_timeout);

        _logger?.LogWarning(
            "Background execution: Sub-agent permission for '{Agent}' auto-rejected (timeout: {Timeout}s)",
            agentName,
            _timeout.TotalSeconds);

        return PermissionDecision.Deny(
            $"Background execution mode: Permission request timed out after {_timeout.TotalSeconds} seconds");
    }

    /// <inheritdoc/>
    public async Task<PermissionDecision> RequestWritePermissionAsync(
        string filePath,
        string? contentPreview,
        AgentContext context)
    {
        _logger?.LogWarning(
            "Background execution: Write permission request for '{File}' will be auto-rejected after {Timeout}s",
            filePath,
            _timeout.TotalSeconds);

        // Wait for timeout, then auto-reject
        await Task.Delay(_timeout);

        _logger?.LogWarning(
            "Background execution: Write permission for '{File}' auto-rejected (timeout: {Timeout}s)",
            filePath,
            _timeout.TotalSeconds);

        return PermissionDecision.Deny(
            $"Background execution mode: Permission request timed out after {_timeout.TotalSeconds} seconds");
    }
}