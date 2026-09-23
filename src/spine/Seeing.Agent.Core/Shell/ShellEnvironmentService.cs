using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Hooks;

namespace Seeing.Agent.Core.Shell;

/// <summary>
/// Shell 环境服务实现 - 通过 Hook 机制允许外部注入环境变量
/// </summary>
public class ShellEnvironmentService : IShellEnvironmentService
{
    private readonly ILogger<ShellEnvironmentService> _logger;
    private readonly IHookManager _hookManager;

    /// <summary>初始化 Shell 环境服务，注入日志器与 Hook 管理器。</summary>
    public ShellEnvironmentService(
        ILogger<ShellEnvironmentService> logger,
        IHookManager hookManager)
    {
        _logger = logger;
        _hookManager = hookManager;
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, string>> GetEnvironmentAsync(
        string cwd,
        string? sessionId = null,
        string? callId = null,
        CancellationToken cancellationToken = default)
    {
        var output = new Dictionary<string, object?>
        {
            ["env"] = new Dictionary<string, string>()
        };

        await _hookManager.TriggerBlockingAsync(
            HookRegistry.ShellEnv,
            sessionId ?? string.Empty,
            new Dictionary<string, object?>
            {
                ["cwd"] = cwd,
                ["callId"] = callId ?? string.Empty
            },
            output,
            cancellationToken);

        if (output.TryGetValue("env", out var envObj) && envObj is Dictionary<string, string> env)
        {
            _logger.LogDebug("Shell 环境变量已通过 Hook 注入: {Count} 个", env.Count);
            return env;
        }

        if (envObj is IDictionary<string, object> envDict)
        {
            var result = new Dictionary<string, string>();
            foreach (var (key, value) in envDict)
            {
                if (value != null)
                {
                    result[key] = value.ToString() ?? string.Empty;
                }
            }
            return result;
        }

        return new Dictionary<string, string>();
    }
}
