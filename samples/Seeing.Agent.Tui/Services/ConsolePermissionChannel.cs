using Seeing.Agent.Core.Interfaces;
using Spectre.Console;

namespace Seeing.Agent.Tui.Services;

/// <summary>
/// 在终端用 Spectre 确认权限（RuleEngine Ask）。
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
        var msg = $"权限请求: {request.Permission} — {patterns}\n是否允许？";
        var ok = AnsiConsole.Confirm(msg, defaultValue: false);
        return Task.FromResult(ok);
    }

    /// <inheritdoc />
    public void SetConfirmationHandler(Func<PermissionRequest, Task<bool>> handler) =>
        _handler = handler;
}
