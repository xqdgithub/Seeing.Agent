using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Abstractions.Mcp;

namespace Seeing.Agent.Mcp.Commands;

/// <summary>
/// 将已连接且声明 prompts 的 MCP server 的 prompt 登记为斜杠命令；
/// Disconnect / Disable / Deactivate 时按 server 撤销。重名 <see cref="ICommandRegistry.HasCommand"/> 则跳过。
/// </summary>
internal sealed class McpPromptCommandSync : IDisposable
{
    private readonly IMcpManager _manager;
    private readonly ICommandRegistry _registry;
    private readonly Func<string, IMcpSession?> _resolveSession;
    private readonly ILogger _logger;

    /// <summary>commandName → serverName（本同步器登记过的集合）。</summary>
    private readonly Dictionary<string, string> _registered = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private bool _started;
    private bool _disposed;

    public McpPromptCommandSync(
        IMcpManager manager,
        ICommandRegistry registry,
        Func<string, IMcpSession?> resolveSession,
        ILogger logger)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _resolveSession = resolveSession ?? throw new ArgumentNullException(nameof(resolveSession));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>订阅状态变更并对当前已连接 server 对齐一次。</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _started)
                return;

            _started = true;
            _manager.StatusChanged += OnStatusChanged;
            AlignAllLocked();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            if (_started)
                _manager.StatusChanged -= OnStatusChanged;

            UnregisterAllLocked();
            _started = false;
        }
    }

    private void OnStatusChanged(object? sender, McpStatusChangedEventArgs e)
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            AlignServerLocked(e.ServerName, e.Status);
        }
    }

    private void AlignAllLocked()
    {
        // Moq / 精简 Activate 测试可能未桩 GetAllStatus；空表视为无 server。
        var all = _manager.GetAllStatus();
        if (all is null || all.Count == 0)
            return;

        foreach (var (name, status) in all)
        {
            if (status is null)
                continue;
            AlignServerLocked(name, status);
        }
    }

    private void AlignServerLocked(string serverName, McpServerStatus status)
    {
        var shouldExpose = status.IsAvailable
            && status.Capabilities?.Prompts == true;

        if (!shouldExpose)
        {
            UnregisterServerLocked(serverName);
            return;
        }

        var session = _resolveSession(serverName);
        if (session is null)
        {
            UnregisterServerLocked(serverName);
            return;
        }

        McpPromptListResult list;
        try
        {
            // 同步对齐路径：list 通常很快；失败则清空该 server 命令并记日志。
            list = session.ListPromptsAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "列举 MCP prompts 失败，已跳过命令同步：{Server}", serverName);
            UnregisterServerLocked(serverName);
            return;
        }

        if (list.NotSupported || !string.IsNullOrEmpty(list.ErrorMessage))
        {
            if (!string.IsNullOrEmpty(list.ErrorMessage))
                _logger.LogWarning("列举 MCP prompts 失败（{Server}）：{Error}", serverName, list.ErrorMessage);
            UnregisterServerLocked(serverName);
            return;
        }

        var desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prompt in list.Items)
        {
            if (string.IsNullOrWhiteSpace(prompt.Name))
                continue;

            var commandName = McpPromptCommandNames.Build(serverName, prompt.Name);
            desired.Add(commandName);

            if (_registered.ContainsKey(commandName))
                continue;

            if (_registry.HasCommand(commandName))
            {
                _logger.LogWarning(
                    "MCP prompt 命令与已有命令重名，已跳过：{CommandName}（server={Server}, prompt={Prompt}）",
                    commandName, serverName, prompt.Name);
                continue;
            }

            try
            {
                _registry.Register(new McpPromptCommand(serverName, prompt, _resolveSession));
                _registered[commandName] = serverName;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "登记 MCP prompt 命令失败：{CommandName}", commandName);
            }
        }

        foreach (var name in _registered
                     .Where(kv => string.Equals(kv.Value, serverName, StringComparison.OrdinalIgnoreCase)
                                  && !desired.Contains(kv.Key))
                     .Select(kv => kv.Key)
                     .ToList())
        {
            UnregisterIfOwnedLocked(name);
            _registered.Remove(name);
        }
    }

    private void UnregisterServerLocked(string serverName)
    {
        foreach (var name in _registered
                     .Where(kv => string.Equals(kv.Value, serverName, StringComparison.OrdinalIgnoreCase))
                     .Select(kv => kv.Key)
                     .ToList())
        {
            UnregisterIfOwnedLocked(name);
            _registered.Remove(name);
        }
    }

    private void UnregisterAllLocked()
    {
        foreach (var name in _registered.Keys.ToList())
            UnregisterIfOwnedLocked(name);
        _registered.Clear();
    }

    private void UnregisterIfOwnedLocked(string name)
    {
        try
        {
            if (_registry.GetCommand(name) is McpPromptCommand)
                _registry.Unregister(name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "撤销 MCP prompt 命令失败：{CommandName}", name);
        }
    }
}
