using Seeing.Agent.Abstractions.Mcp;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Mcp.Configuration;
using Seeing.Agent.Abstractions.Configuration;

namespace Seeing.Agent.Mcp;

/// <summary>
/// MCP 配置加载器 - 静态便捷入口，委托给 McpConfigPersistence
/// <para>
/// 配置文件位置：
/// - 用户级：~/.seeing/mcp.json
/// - 项目级：./.seeing/mcp.json
/// 项目级同名服务覆盖用户级
/// </para>
/// </summary>
public static class McpConfigLoader
{
    /// <summary>
    /// 加载默认路径的 MCP 配置（异步版本）
    /// </summary>
    public static async Task<IReadOnlyList<McpServerConfig>> LoadDefaultAsync(
        string workspaceRoot,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        return await LoadDefaultAsync(new WorkspaceSeeingDirectories(workspaceRoot), logger, cancellationToken);
    }

    /// <summary>
    /// 加载默认路径的 MCP 配置（异步版本，使用 <see cref="ISeeingDirectories"/>）
    /// </summary>
    public static async Task<IReadOnlyList<McpServerConfig>> LoadDefaultAsync(
        ISeeingDirectories directories,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        var persistence = new McpConfigPersistence(
            logger as ILogger<McpConfigPersistence> ?? new NullLogger<McpConfigPersistence>(),
            directories);

        var configs = new Dictionary<string, McpServerConfig>(StringComparer.OrdinalIgnoreCase);

        // 先加载用户级
        if (persistence.ConfigExists(ConfigLevel.User))
        {
            var userConfigs = await persistence.LoadAsync(ConfigLevel.User, cancellationToken);
            foreach (var kvp in userConfigs)
                configs[kvp.Key] = kvp.Value;
        }

        // 后加载项目级
        if (persistence.ConfigExists(ConfigLevel.Project))
        {
            var projectConfigs = await persistence.LoadAsync(ConfigLevel.Project, cancellationToken);
            foreach (var kvp in projectConfigs)
                configs[kvp.Key] = kvp.Value;
        }

        return configs.Values.ToList().AsReadOnly();
    }
}

/// <summary>
/// 空 Logger 实现（用于无 logger 场景）
/// </summary>
file class NullLogger<T> : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => false;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
}