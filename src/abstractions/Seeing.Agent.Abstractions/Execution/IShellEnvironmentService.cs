namespace Seeing.Agent.Abstractions.Execution;

/// <summary>
/// Shell 环境服务 — 在执行 shell 命令前获取环境变量（触发 shell.env Hook）。
/// </summary>
public interface IShellEnvironmentService
{
    /// <summary>
    /// 获取 Shell 环境变量。
    /// </summary>
    Task<Dictionary<string, string>> GetEnvironmentAsync(
        string cwd,
        string? sessionId = null,
        string? callId = null,
        CancellationToken cancellationToken = default);
}
