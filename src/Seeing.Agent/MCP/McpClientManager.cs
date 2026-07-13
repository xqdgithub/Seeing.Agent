using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Helpers;
using Seeing.Agent.MCP.Configuration;
using Seeing.Agent.MCP.Core;
using Seeing.Agent.MCP.Factory;
using Seeing.Agent.MCP.Management;
using Seeing.Agent.MCP.Policy;
using Seeing.Agent.MCP.Validation;
using Seeing.Agent.Tools;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

using CoreMcpConnectionState = Seeing.Agent.MCP.Core.McpConnectionState;

namespace Seeing.Agent.MCP
{
    public sealed class McpClientManager : IMcpManager
    {
        private readonly ILogger<McpClientManager> _logger;
        private readonly ILoggerFactory _loggerFactory;
        private readonly IHookManager _hookManager;
        private readonly ToolInvoker _toolInvoker;
        private readonly McpWrapperFactoryRegistry _factoryRegistry;
        private readonly McpGlobalPolicy _globalPolicy;
        private readonly IMcpConfigPersistence _configPersistence;
        private readonly IHttpClientFactory? _httpClientFactory;

        private readonly ConcurrentDictionary<string, McpServerStatus> _statuses = new();
        private readonly ConcurrentDictionary<string, McpServerConfig> _configs = new();
        private readonly ConcurrentDictionary<string, McpConnectionCoordinator> _coordinators = new();
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _reconnectLocks = new();

        private readonly McpToolRegistry _toolRegistry;
        private readonly McpBackgroundReconnector _reconnector;
        private readonly McpProcessMonitor _processMonitor;

        private readonly CancellationTokenSource _shutdownCts = new();
        private readonly object _stateLock = new();
        private volatile bool _initialized;
        private bool _disposed;

        public event EventHandler<McpStatusChangedEventArgs>? StatusChanged;

        public McpClientManager(
            ILogger<McpClientManager> logger,
            ILoggerFactory loggerFactory,
            IHookManager hookManager,
            ToolInvoker toolInvoker,
            McpWrapperFactoryRegistry factoryRegistry,
            McpGlobalPolicy globalPolicy,
            IMcpConfigPersistence configPersistence,
            IHttpClientFactory? httpClientFactory = null)
        {
            _logger = logger;
            _loggerFactory = loggerFactory;
            _hookManager = hookManager;
            _toolInvoker = toolInvoker;
            _factoryRegistry = factoryRegistry;
            _globalPolicy = globalPolicy;
            _configPersistence = configPersistence;
            _httpClientFactory = httpClientFactory;

            _toolRegistry = new McpToolRegistry(_toolInvoker, _hookManager, logger);
            _processMonitor = new McpProcessMonitor(logger);
            _reconnector = new McpBackgroundReconnector(
                logger,
                _globalPolicy,
                async name =>
                {
                    if (_coordinators.TryGetValue(name, out var coordinator))
                        return await coordinator.ReconnectAsync(_shutdownCts.Token);
                    return McpOperationResult.Failed(name, McpOperationType.Reconnect, McpErrorInfo.ServerRemoved(name));
                },
                GetAllStatus);
        }

        #region 兼容性构造函数（旧版）

        private static HookManager? s_defaultHookManager;
        private static ToolInvoker? s_defaultToolInvoker;

        public McpClientManager(
            ILogger<McpClientManager> logger,
            ILoggerFactory loggerFactory,
            IHttpClientFactory? httpClientFactory = null)
            : this(
                logger,
                loggerFactory,
                GetOrCreateDefaultHookManager(loggerFactory),
                GetOrCreateDefaultToolInvoker(loggerFactory),
                CreateDefaultFactoryRegistry(loggerFactory),
                new McpGlobalPolicy(),
                CreateDefaultConfigPersistence(loggerFactory),
                httpClientFactory)
        {
        }

        private static HookManager GetOrCreateDefaultHookManager(ILoggerFactory loggerFactory)
        {
            if (s_defaultHookManager == null)
                s_defaultHookManager = new HookManager(loggerFactory.CreateLogger<HookManager>());
            return s_defaultHookManager;
        }

        private static ToolInvoker GetOrCreateDefaultToolInvoker(ILoggerFactory loggerFactory)
        {
            if (s_defaultToolInvoker == null)
            {
                var hookManager = GetOrCreateDefaultHookManager(loggerFactory);
                s_defaultToolInvoker = new ToolInvoker(
                    loggerFactory.CreateLogger<ToolInvoker>(),
                    hookManager);
            }
            return s_defaultToolInvoker;
        }

        private static McpWrapperFactoryRegistry CreateDefaultFactoryRegistry(ILoggerFactory loggerFactory)
        {
            var registry = new McpWrapperFactoryRegistry();
            registry.Register(new StdioWrapperFactory(loggerFactory));
            registry.Register(new StreamableHttpWrapperFactory(loggerFactory));
            registry.Register(new SseWrapperFactory(loggerFactory));
            return registry;
        }

        private static McpConfigPersistence CreateDefaultConfigPersistence(ILoggerFactory loggerFactory)
            => new(loggerFactory.CreateLogger<McpConfigPersistence>(), new WorkspaceProvider());

        #endregion

        #region IMcpStatusProvider 实现

        public IReadOnlyDictionary<string, McpServerStatus> GetAllStatus()
        {
            lock (_stateLock)
            {
                return _statuses.ToDictionary(k => k.Key, v => v.Value.Clone());
            }
        }

        public McpServerStatus? GetStatus(string name)
        {
            lock (_stateLock)
            {
                return _statuses.TryGetValue(name, out var status) ? status.Clone() : null;
            }
        }

        public IReadOnlyList<Core.McpToolInfo> GetTools()
        {
            return _toolRegistry.GetTools()
                .Select(t => new Core.McpToolInfo
                {
                    Name = t.ToolName,
                    ServerName = t.ServerName,
                    Description = t.Description,
                    Schema = t.ParametersSchema.ToDictionary()
                })
                .ToList();
        }

        public bool IsAvailable(string name)
        {
            var status = GetStatus(name);
            return status?.IsAvailable ?? false;
        }

        public IReadOnlyList<string> GetAvailableServers()
        {
            return GetAllStatus()
                .Where(s => s.Value.IsAvailable)
                .Select(s => s.Key)
                .ToList();
        }

        #endregion

        #region IMcpController 实现

        public async Task<McpOperationResult> ConnectServerAsync(string name, CancellationToken cancellationToken = default)
        {
            var config = GetConfig(name);
            if (config == null)
                return McpOperationResult.Failed(name, McpOperationType.Connect, McpErrorInfo.ConfigMissing(name));

            if (config.Disabled)
                return McpOperationResult.Failed(name, McpOperationType.Connect,
                    McpErrorInfo.ConfigInvalid(name, "服务器已禁用"));

            var coordinator = GetOrCreateCoordinator(name);
            return await coordinator.ConnectAsync(config, cancellationToken);
        }

        public async Task<McpOperationResult> DisconnectServerAsync(string name, CancellationToken cancellationToken = default)
        {
            if (!_coordinators.TryGetValue(name, out var coordinator))
                return McpOperationResult.NoChange(name, McpOperationType.Disconnect, CoreMcpConnectionState.Pending);

            var result = await coordinator.DisconnectAsync(cancellationToken);

            if (result.Success)
            {
                _processMonitor.Unwatch(name);
            }

            return result;
        }

        public async Task<McpOperationResult> ReconnectServerAsync(string name, CancellationToken cancellationToken = default)
        {
            var config = GetConfig(name);
            if (config?.Disabled == true)
                return McpOperationResult.Failed(name, McpOperationType.Reconnect,
                    McpErrorInfo.ConfigInvalid(name, "服务器已禁用"));

            if (!_coordinators.TryGetValue(name, out var coordinator))
                return McpOperationResult.Failed(name, McpOperationType.Reconnect, McpErrorInfo.ServerRemoved(name));

            _reconnector.ResetReconnectTime(name);
            return await coordinator.ReconnectAsync(cancellationToken);
        }

        public McpOperationResult PauseServer(string name)
        {
            if (!_coordinators.TryGetValue(name, out var coordinator))
                return McpOperationResult.Failed(name, McpOperationType.Pause,
                    McpErrorInfo.ConfigInvalid(name, "服务器未连接，无法暂停"));

            return coordinator.PauseAsync(_shutdownCts.Token).GetAwaiter().GetResult();
        }

        public McpOperationResult ResumeServer(string name)
        {
            if (!_coordinators.TryGetValue(name, out var coordinator))
                return McpOperationResult.Failed(name, McpOperationType.Resume,
                    McpErrorInfo.ConfigInvalid(name, "服务器未连接，无法恢复"));

            return coordinator.ResumeAsync(_shutdownCts.Token).GetAwaiter().GetResult();
        }

        public int PauseAllServers()
        {
            var count = 0;
            foreach (var coordinator in _coordinators.Values)
            {
                var result = coordinator.PauseAsync(_shutdownCts.Token).GetAwaiter().GetResult();
                if (result.Success) count++;
            }
            return count;
        }

        public int ResumeAllServers()
        {
            var count = 0;
            foreach (var coordinator in _coordinators.Values)
            {
                var result = coordinator.ResumeAsync(_shutdownCts.Token).GetAwaiter().GetResult();
                if (result.Success) count++;
            }
            return count;
        }

        public async Task<bool> WaitForReadyAsync(string name, int timeoutMs = 30000, CancellationToken cancellationToken = default)
        {
            if (!_coordinators.TryGetValue(name, out var coordinator))
                return false;

            return await coordinator.WaitForReadyAsync(TimeSpan.FromMilliseconds(timeoutMs), cancellationToken);
        }

        #endregion

        #region IMcpConfigManager 实现

        public async Task<McpOperationResult> AddServerAsync(string name, McpServerConfig config, ConfigLevel? level = null, bool persist = true, CancellationToken cancellationToken = default)
        {
            config.Name = name;
            config.ConfigLevel = level ?? ConfigLevel.Project;
            var validation = McpConfigValidator.Validate(config);
            if (!validation.IsValid)
                return McpOperationResult.Failed(name, McpOperationType.Add, validation.Error!);

            lock (_stateLock)
            {
                _configs[name] = config;
                _statuses[name] = CreateInitialStatus(name, config);
            }

            // 自动持久化
            if (persist)
            {
                try
                {
                    await SaveConfigAsync(config.ConfigLevel.Value, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "持久化 MCP 配置失败 (AddServer: {Name})", name);
                }
            }

            if (_globalPolicy.AutoStartOnAdd && !config.Disabled)
            {
                var coordinator = GetOrCreateCoordinator(name);
                _ = coordinator.ConnectInBackgroundAsync(config, cancellationToken);
                return McpOperationResult.Succeeded(name, McpOperationType.Add, GetStatus(name)?.State);
            }

            return McpOperationResult.Succeeded(name, McpOperationType.Add, GetStatus(name)?.State ?? CoreMcpConnectionState.Pending);
        }

        public async Task<McpOperationResult> RemoveServerAsync(string name, bool persist = true, CancellationToken cancellationToken = default)
        {
            var config = GetConfig(name);
            if (config == null)
                return McpOperationResult.NoChange(name, McpOperationType.Remove, CoreMcpConnectionState.Removed);

            var configLevel = config.ConfigLevel ?? ConfigLevel.Project;
            var disconnectResult = await DisconnectServerAsync(name, cancellationToken);

            lock (_stateLock)
            {
                _configs.TryRemove(name, out _);
                _statuses.TryRemove(name, out _);
                _coordinators.TryRemove(name, out _);
            }

            await _toolRegistry.UnregisterAllToolsAsync(name);

            // 自动持久化
            if (persist)
            {
                try
                {
                    await SaveConfigAsync(configLevel, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "持久化 MCP 配置失败 (RemoveServer: {Name})", name);
                }
            }

            return McpOperationResult.Succeeded(name, McpOperationType.Remove, CoreMcpConnectionState.Removed);
        }

        public async Task<McpOperationResult> UpdateConfigAsync(string name, McpServerConfig config, bool persist = true, CancellationToken cancellationToken = default)
        {
            config.Name = name;
            var validation = McpConfigValidator.Validate(config);
            if (!validation.IsValid)
                return McpOperationResult.Failed(name, McpOperationType.UpdateConfig, validation.Error!);

            var beforeInput = new Dictionary<string, object?>
            {
                ["serverName"] = name,
                ["oldConfig"] = GetConfig(name),
                ["newConfig"] = config
            };
            var beforeResult = await _hookManager.TriggerBlockingAsync(
                HookRegistry.McpBeforeConfigUpdate, "", beforeInput, null, cancellationToken);

            if (!beforeResult.Continue)
                return McpOperationResult.Failed(name, McpOperationType.UpdateConfig,
                    McpErrorInfo.ConfigInvalid(name, "Hook 拒绝配置更新"));

            var wasConnected = IsAvailable(name);

            if (wasConnected)
            {
                await DisconnectServerAsync(name, cancellationToken);
            }

            // 保留原有的 ConfigLevel
            var existingConfig = GetConfig(name);
            config.ConfigLevel = existingConfig?.ConfigLevel ?? ConfigLevel.Project;

            lock (_stateLock)
            {
                _configs[name] = config;
                var existingStatus = GetStatus(name);
                if (existingStatus != null)
                {
                    _statuses[name] = McpServerStatusBuilder.From(existingStatus)
                        .WithConfig(config)
                        .Build();
                }
                else
                {
                    _statuses[name] = McpServerStatus.Create(name, config, GetPriorityFromConfig(config));
                }
            }

            // 自动持久化
            if (persist)
            {
                try
                {
                    await SaveConfigAsync(config.ConfigLevel.Value, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "持久化 MCP 配置失败 (UpdateConfig: {Name})", name);
                }
            }

            if (wasConnected)
            {
                await ConnectServerAsync(name, cancellationToken);
            }

            return McpOperationResult.Succeeded(name, McpOperationType.UpdateConfig, GetStatus(name)?.State);
        }

        public async Task<int> ReloadAllAsync(CancellationToken cancellationToken = default)
        {
            var reloadedCount = 0;
            var configNames = _configs.Keys.ToList();

            foreach (var name in configNames)
            {
                try
                {
                    var result = await ReconnectServerAsync(name, cancellationToken);
                    if (result.Success) reloadedCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "重载 MCP Server {Name} 失败", name);
                }
            }

            return reloadedCount;
        }

        public (bool Valid, string? Error) ValidateConfig(string name, McpServerConfig config)
        {
            config.Name = name;
            var result = McpConfigValidator.Validate(config);
            return (result.IsValid, result.Error?.UserMessage);
        }

        public McpServerConfig? GetConfig(string name)
        {
            lock (_stateLock)
            {
                return _configs.TryGetValue(name, out var config) ? config : null;
            }
        }

        public async Task SaveConfigAsync(ConfigLevel level, CancellationToken cancellationToken = default)
        {
            // 加锁获取配置快照，避免迭代期间被修改
            Dictionary<string, McpServerConfig> configsToSave;
            lock (_stateLock)
            {
                configsToSave = _configs
                    .Where(kvp => kvp.Value.ConfigLevel == level)
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            }

            await _configPersistence.SaveAsync(level, configsToSave, cancellationToken);
            _logger.LogInformation("已保存 {Count} 个 MCP 配置到 {Level} 级别", configsToSave.Count, level);
        }

        /// <inheritdoc />
        public IReadOnlyDictionary<string, McpServerConfig> GetAllConfigs()
        {
            lock (_stateLock)
            {
                return new Dictionary<string, McpServerConfig>(_configs);
            }
        }

        /// <inheritdoc />
        public ConfigLevel? GetConfigLevel(string name)
        {
            lock (_stateLock)
            {
                if (_configs.TryGetValue(name, out var config))
                    return config.ConfigLevel ?? ConfigLevel.Project;
                return null;
            }
        }

        /// <inheritdoc />
        public string GetConfigFilePath(ConfigLevel level)
        {
            return _configPersistence.GetConfigPath(level);
        }

        /// <inheritdoc />
        public string GetConfigsAsJson(ConfigLevel level, bool indented = true)
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = indented,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            var configs = GetAllConfigs();
            var wrapper = new { mcpServers = configs };
            return JsonSerializer.Serialize(wrapper, options);
        }

        /// <inheritdoc />
        public string? GetServerConfigAsJson(string name, bool indented = true)
        {
            var config = GetConfig(name);
            if (config == null) return null;

            return _configPersistence.SerializeServerConfig(config);
        }

        /// <inheritdoc />
        public async Task<McpOperationResult> EnableServerAsync(string name, bool persist = true, CancellationToken cancellationToken = default)
        {
            try
            {
                McpServerConfig? config;
                bool wasDisabled;

                lock (_stateLock)
                {
                    config = GetConfig(name);
                    if (config == null)
                        return McpOperationResult.Failed(name, McpOperationType.UpdateConfig, McpErrorInfo.ConfigMissing(name));

                    wasDisabled = config.Disabled;
                    if (!wasDisabled)
                        return McpOperationResult.NoChange(name, McpOperationType.UpdateConfig, GetStatus(name)?.State ?? CoreMcpConnectionState.Pending);

                    config.Disabled = false;
                }

                // 自动持久化
                if (persist)
                {
                    try
                    {
                        var configLevel = config.ConfigLevel ?? ConfigLevel.Project;
                        await SaveConfigAsync(configLevel, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "持久化 MCP 配置失败 (EnableServer: {Name})", name);
                    }
                }

                // 启用 = 清除 Disabled 标志并启动连接（不依赖 AutoStartOnAdd，也不走 Resume）
                return await StartServerConnectionAsync(name, config, cancellationToken);
            }
            catch (Exception ex)
            {
                return McpOperationResult.Failed(name, McpOperationType.UpdateConfig,
                    McpErrorInfo.FromException(McpErrorCode.ToolExecutionError, ex, name));
            }
        }

        /// <inheritdoc />
        public async Task<McpOperationResult> DisableServerAsync(string name, bool persist = true, CancellationToken cancellationToken = default)
        {
            try
            {
                McpServerConfig? config;
                bool wasEnabled;

                lock (_stateLock)
                {
                    config = GetConfig(name);
                    if (config == null)
                        return McpOperationResult.Failed(name, McpOperationType.UpdateConfig, McpErrorInfo.ConfigMissing(name));

                    wasEnabled = !config.Disabled;
                    if (!wasEnabled)
                        return McpOperationResult.NoChange(name, McpOperationType.UpdateConfig, GetStatus(name)?.State ?? CoreMcpConnectionState.Pending);

                    config.Disabled = true;
                }

                // 自动持久化
                if (persist)
                {
                    try
                    {
                        var configLevel = config.ConfigLevel ?? ConfigLevel.Project;
                        await SaveConfigAsync(configLevel, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "持久化 MCP 配置失败 (DisableServer: {Name})", name);
                    }
                }

                var status = GetStatus(name);

                if (status?.State is CoreMcpConnectionState.Connected
                    or CoreMcpConnectionState.Connecting
                    or CoreMcpConnectionState.Reconnecting)
                {
                    var disconnectResult = await DisconnectServerAsync(name, cancellationToken);
                    if (!disconnectResult.Success && status.State == CoreMcpConnectionState.Connected)
                        return disconnectResult;
                }

                await _toolRegistry.UnregisterAllToolsAsync(name);
                ApplyDisabledStatus(name, config);

                return McpOperationResult.Succeeded(name, McpOperationType.UpdateConfig, CoreMcpConnectionState.Disabled);
            }
            catch (Exception ex)
            {
                return McpOperationResult.Failed(name, McpOperationType.UpdateConfig,
                    McpErrorInfo.FromException(McpErrorCode.ToolExecutionError, ex, name));
            }
        }

        /// <inheritdoc />
        public async Task<int> ImportFromJsonAsync(string json, ConfigLevel level, bool overwrite = false, bool persist = true, CancellationToken cancellationToken = default)
        {
            // 输入验证：空值检查
            if (string.IsNullOrWhiteSpace(json))
            {
                _logger.LogWarning("导入 MCP 配置失败：JSON 为空");
                return 0;
            }

            // JSON 格式验证
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json, new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "导入 MCP 配置失败：JSON 格式无效 - {Error}", ex.Message);
                throw new JsonException($"JSON 格式错误: {ex.Message}", ex);
            }

            using var _ = doc;

            if (!doc.RootElement.TryGetProperty("mcpServers", out var servers) ||
                servers.ValueKind != JsonValueKind.Object)
            {
                _logger.LogWarning("导入 MCP 配置失败：缺少 mcpServers 对象");
                return 0;
            }

            var count = 0;
            foreach (var prop in servers.EnumerateObject())
            {
                var name = prop.Name;
                if (!overwrite && _configs.ContainsKey(name))
                    continue;

                try
                {
                    var config = _configPersistence.ParseServerConfig(name, prop.Value);
                    if (config != null && config.IsValid())
                    {
                        config.ConfigLevel = level;
                        // persist: false 因为 ImportFromJsonAsync 会在最后统一持久化
                        var result = await AddServerAsync(name, config, level, persist: false, cancellationToken);
                        if (result.Success) count++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "导入 MCP 服务 {Name} 失败", name);
                }
            }

            // 导入成功后自动持久化
            if (persist && count > 0)
            {
                try
                {
                    await SaveConfigAsync(level, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "持久化 MCP 配置到 {Level} 级别失败", level);
                }
            }

            return count;
        }

        private static McpServerPriority GetPriorityFromConfig(McpServerConfig config)
        {
            return McpServerPriority.Normal;
        }

        private static McpServerStatus CreateInitialStatus(string name, McpServerConfig config)
        {
            var status = McpServerStatus.Create(name, config, GetPriorityFromConfig(config));
            if (config.Disabled)
            {
                return McpServerStatusBuilder.From(status)
                    .WithState(CoreMcpConnectionState.Disabled)
                    .Build();
            }

            return status;
        }

        private void ApplyDisabledStatus(string name, McpServerConfig config)
        {
            lock (_stateLock)
            {
                var currentStatus = GetStatus(name);
                if (currentStatus == null || currentStatus.State == CoreMcpConnectionState.Disabled)
                    return;

                McpStateTransitions.ValidateTransition(currentStatus.State, CoreMcpConnectionState.Disabled);

                var disabledStatus = McpServerStatusBuilder.From(currentStatus)
                    .WithConfig(config)
                    .WithState(CoreMcpConnectionState.Disabled)
                    .WithToolCount(0)
                    .WithToolNames(Array.Empty<string>())
                    .Build();
                _statuses[name] = disabledStatus;
                OnStatusChanged(name, CoreMcpConnectionState.Disabled, currentStatus);
            }
        }

        private void TransitionToConnecting(string name, McpServerConfig config)
        {
            lock (_stateLock)
            {
                var current = GetStatus(name);
                if (current == null)
                    return;

                if (current.State == CoreMcpConnectionState.Connecting)
                    return;

                McpStateTransitions.ValidateTransition(current.State, CoreMcpConnectionState.Connecting);

                var connecting = McpServerStatusBuilder.From(current)
                    .WithConfig(config)
                    .WithState(CoreMcpConnectionState.Connecting)
                    .WithToolCount(0)
                    .WithToolNames(Array.Empty<string>())
                    .Build();
                _statuses[name] = connecting;
                OnStatusChanged(name, CoreMcpConnectionState.Connecting, current);
            }
        }

        private Task<McpOperationResult> StartServerConnectionAsync(
            string name,
            McpServerConfig config,
            CancellationToken cancellationToken)
        {
            TransitionToConnecting(name, config);

            var coordinator = GetOrCreateCoordinator(name);
            _ = coordinator.ConnectInBackgroundAsync(config, cancellationToken);
            return Task.FromResult(
                McpOperationResult.Succeeded(name, McpOperationType.UpdateConfig, CoreMcpConnectionState.Connecting));
        }

        #endregion

        #region IMcpManager 核心方法

        public async Task InitializeAsync(IReadOnlyDictionary<string, McpServerConfig> configs, CancellationToken cancellationToken = default)
        {
            // 使用 Interlocked.CompareExchange 确保原子性
            if (Interlocked.CompareExchange(ref _initialized, true, false))
                return;

            var beforeInput = new Dictionary<string, object?> { ["configCount"] = configs.Count };
            await _hookManager.TriggerBlockingAsync(HookRegistry.McpBeforeInitialize, "", beforeInput, null, cancellationToken);

            foreach (var kvp in configs)
            {
                var name = kvp.Key;
                var config = kvp.Value;
                config.Name = name;

                var validation = McpConfigValidator.Validate(config);
                if (!validation.IsValid)
                {
                    _logger.LogWarning("MCP Server {Name} 配置无效: {Error}", name, validation.Error?.UserMessage);
                    continue;
                }

                lock (_stateLock)
                {
                    _configs[name] = config;
                    _statuses[name] = CreateInitialStatus(name, config);
                }

                // 只启动未被禁用的服务器
                if (!config.Disabled)
                {
                    var coordinator = GetOrCreateCoordinator(name);
                    _ = coordinator.ConnectInBackgroundAsync(config, cancellationToken);
                }
            }

            _reconnector.Start(_shutdownCts.Token);

            _logger.LogInformation("MCP Manager 已初始化，共 {Count} 个服务器配置", configs.Count);
        }

        public async Task ShutdownAsync(CancellationToken cancellationToken = default)
        {
            if (!_initialized) return;

            _reconnector.Stop();
            _processMonitor.Stop();

            foreach (var coordinator in _coordinators.Values.ToList())
            {
                try
                {
                    await coordinator.DisconnectAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "断开 MCP Server 连接时发生错误");
                }
            }

            lock (_stateLock)
            {
                foreach (var name in _statuses.Keys.ToList())
                {
                    var current = _statuses[name];
                    _statuses[name] = McpServerStatusBuilder.From(current)
                        .WithState(CoreMcpConnectionState.Pending)
                        .Build();
                }
            }

            _hookManager.TriggerFireAndForget(HookRegistry.McpShutdown, "",
                new Dictionary<string, object?> { ["timestamp"] = DateTimeOffset.Now });

            _logger.LogInformation("MCP Manager 已关闭");
        }

        #endregion

        #region 兼容性方法（旧版 API）

        public IReadOnlyCollection<McpTool> GetMcpTools() => _toolRegistry.GetTools();

        public IEnumerable<ITool> GetToolsAsITools() => _toolRegistry.GetTools().Cast<ITool>();

        public async Task ConnectAsync(McpServerConfig config, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(config.Name))
            {
                _logger.LogWarning("MCP Server 配置缺少名称，已跳过");
                return;
            }

            if (!config.IsValid())
            {
                _logger.LogWarning("MCP Server {Name} 配置无效，已跳过", config.Name);
                return;
            }

            // persist: false 因为这是从配置文件加载，不需要再保存
            var result = await AddServerAsync(config.Name, config, null, persist: false, cancellationToken);

            if (!result.Success)
            {
                _logger.LogError("连接 MCP Server {Name} 失败: {Error}", config.Name, result.Error?.UserMessage);
                throw new InvalidOperationException(result.Error?.UserMessage ?? "连接失败");
            }

            var coordinator = GetOrCreateCoordinator(config.Name);
            await coordinator.WaitForReadyAsync(_globalPolicy.WaitForReadyTimeout, cancellationToken);
        }

        public async Task ConnectAsync(IEnumerable<McpServerConfig> configs, CancellationToken cancellationToken = default)
        {
            var configDict = new Dictionary<string, McpServerConfig>();
            foreach (var config in configs)
            {
                if (!string.IsNullOrEmpty(config.Name))
                    configDict[config.Name] = config;
            }

            await InitializeAsync(configDict, cancellationToken);
        }

        public async Task DisconnectAllAsync()
        {
            await ShutdownAsync(_shutdownCts.Token);

            lock (_stateLock)
            {
                _configs.Clear();
                _statuses.Clear();
                _coordinators.Clear();
            }

            await _toolRegistry.UnregisterAllToolsAsync("");

            foreach (var s in _reconnectLocks.Values)
            {
                try { s.Dispose(); } catch { }
            }
            _reconnectLocks.Clear();
        }

        public bool IsConnected(string serverName) => IsAvailable(serverName);

        public IReadOnlyCollection<string> GetConnectedServers() => GetAvailableServers();

        #endregion

        #region 内部方法

        internal void UpdateState(string serverName, McpServerStatus newStatus)
        {
            McpServerStatus? previous;
            lock (_stateLock)
            {
                previous = _statuses.TryGetValue(serverName, out var p) ? p : null;
                _statuses[serverName] = newStatus;
            }

            OnStatusChanged(serverName, newStatus.State, previous);
        }

        private void OnStatusChanged(string serverName, CoreMcpConnectionState newState, McpServerStatus? previous)
        {
            var previousState = previous?.State ?? CoreMcpConnectionState.Pending;
            var current = GetStatus(serverName);

            if (current == null) return;

            StatusChanged?.Invoke(this, new McpStatusChangedEventArgs(
                serverName, previousState, newState, current));

            _hookManager.TriggerFireAndForget(HookRegistry.McpStatusChanged, "",
                new Dictionary<string, object?>
                {
                    ["serverName"] = serverName,
                    ["previousState"] = previousState,
                    ["newState"] = newState
                });
        }

        private McpConnectionCoordinator GetOrCreateCoordinator(string serverName)
        {
            return _coordinators.GetOrAdd(serverName, name => new McpConnectionCoordinator(
                name,
                _logger,
                _loggerFactory,
                _httpClientFactory,
                _hookManager,
                _factoryRegistry,
                _toolRegistry,
                _globalPolicy,
                (s, status) => UpdateState(s, status),
                s => GetConfig(s),
                s => GetStatus(s)));
        }

        private async Task<McpToolResult> ExecuteMcpToolAsync(
            string serverName,
            string toolName,
            Dictionary<string, object?> args,
            CancellationToken cancellationToken = default)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var status = GetStatus(serverName);
                if (status == null || !status.IsAvailable)
                {
                    if (status?.CanReconnect == true && attempt == 0)
                    {
                        _logger.LogWarning("MCP Server {Server} 未连接，尝试重连", serverName);
                        await ReconnectServerAsync(serverName, cancellationToken);
                        continue;
                    }
                    return new McpToolResult { IsError = true, Content = $"MCP Server 未连接: {serverName}" };
                }

                if (!_coordinators.TryGetValue(serverName, out var coordinator))
                {
                    return new McpToolResult { IsError = true, Content = $"MCP Server 协调器未找到: {serverName}" };
                }

                var client = coordinator.GetClient();
                if (client == null)
                {
                    return new McpToolResult { IsError = true, Content = $"MCP Server 客户端未初始化: {serverName}" };
                }

                try
                {
                    return await client.CallToolAsync(toolName, args, cancellationToken);
                }
                catch (Exception ex) when (attempt == 0 && IsRecoverableStreamableHttpSessionError(ex))
                {
                    _logger.LogWarning(ex, "MCP HTTP 会话失效（{Server}.{Tool}），尝试重新连接后重试一次", serverName, toolName);

                    var reconnectResult = await ReconnectServerAsync(serverName, cancellationToken);
                    if (!reconnectResult.Success)
                    {
                        return new McpToolResult
                        {
                            IsError = true,
                            Content = $"MCP 重新连接失败，无法重试工具调用: {ex.Message}"
                        };
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "MCP 工具执行失败: {ServerName}.{ToolName}", serverName, toolName);
                    return new McpToolResult { IsError = true, Content = ex.Message };
                }
            }

            return new McpToolResult { IsError = true, Content = "MCP 工具调用重试后仍失败" };
        }

        private static bool IsRecoverableStreamableHttpSessionError(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is not HttpRequestException h) continue;
                if (h.StatusCode == HttpStatusCode.Unauthorized) return true;
                if (h.Message.Contains("SessionExpired", StringComparison.OrdinalIgnoreCase)) return true;
                if (h.Message.Contains("401", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        #endregion

        #region IAsyncDisposable 实现

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                await ShutdownAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "关闭 MCP Manager 时发生错误");
            }

            lock (_stateLock)
            {
                _configs.Clear();
                _statuses.Clear();
                _coordinators.Clear();
            }

            _reconnector.Stop();
            _processMonitor.Stop();

            foreach (var coordinator in _coordinators.Values)
            {
                coordinator.Dispose();
            }

            _shutdownCts.Dispose();
        }

        #endregion
    }

    #region 默认工厂实现

    internal class StdioWrapperFactory : IMcpClientWrapperFactory
    {
        private readonly ILoggerFactory _loggerFactory;

        public McpTransportType TransportType => McpTransportType.Stdio;

        public StdioWrapperFactory(ILoggerFactory loggerFactory) => _loggerFactory = loggerFactory;

        public IMcpClientWrapper Create(McpServerConfig config, IHttpClientFactory? httpClientFactory, ILoggerFactory loggerFactory)
            => new StdioMcpClientWrapper(config, loggerFactory.CreateLogger<StdioMcpClientWrapper>());
    }

    internal class StreamableHttpWrapperFactory : IMcpClientWrapperFactory
    {
        private readonly ILoggerFactory _loggerFactory;

        public McpTransportType TransportType => McpTransportType.StreamableHttp;

        public StreamableHttpWrapperFactory(ILoggerFactory loggerFactory) => _loggerFactory = loggerFactory;

        public IMcpClientWrapper Create(McpServerConfig config, IHttpClientFactory? httpClientFactory, ILoggerFactory loggerFactory)
        {
            if (httpClientFactory == null)
                throw new InvalidOperationException("HTTP 传输需要 IHttpClientFactory");
            return new HttpMcpClientWrapper(config, httpClientFactory, loggerFactory.CreateLogger<HttpMcpClientWrapper>(), HttpTransportMode.StreamableHttp);
        }
    }

    internal class SseWrapperFactory : IMcpClientWrapperFactory
    {
        private readonly ILoggerFactory _loggerFactory;

        public McpTransportType TransportType => McpTransportType.Sse;

        public SseWrapperFactory(ILoggerFactory loggerFactory) => _loggerFactory = loggerFactory;

        public IMcpClientWrapper Create(McpServerConfig config, IHttpClientFactory? httpClientFactory, ILoggerFactory loggerFactory)
        {
            if (httpClientFactory == null)
                throw new InvalidOperationException("HTTP 传输需要 IHttpClientFactory");
            return new HttpMcpClientWrapper(config, httpClientFactory, loggerFactory.CreateLogger<HttpMcpClientWrapper>(), HttpTransportMode.Sse);
        }
    }

    #endregion

    #region 旧版包装器接口和类型（兼容性保留）

    public interface IMcpClientWrapper
    {
        Task ConnectAsync(CancellationToken cancellationToken = default);
        Task DisconnectAsync();
        Task<IReadOnlyList<Seeing.Agent.MCP.Management.McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default);
        Task<McpToolResult> CallToolAsync(string toolName, Dictionary<string, object?> args, CancellationToken cancellationToken = default);
    }

    public class StdioMcpClientWrapper : IMcpClientWrapper
    {
        private readonly McpServerConfig _config;
        private readonly ILogger _logger;
        private McpClient? _mcpClient;
        private StdioClientTransport? _transport;

        public StdioMcpClientWrapper(McpServerConfig config, ILogger logger)
        {
            _config = config;
            _logger = logger;
        }

        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(_config.Command))
                throw new InvalidOperationException("stdio 传输需要配置 command");

            _logger.LogInformation("正在连接 MCP Server (stdio): {Name}, Command: {Command}", _config.Name, _config.Command);

            var transportOptions = new StdioClientTransportOptions
            {
                Name = _config.Name,
                Command = _config.Command,
                Arguments = _config.Args?.ToArray() ?? Array.Empty<string>(),
                ShutdownTimeout = _config.ShutdownTimeout
            };

            if (!string.IsNullOrEmpty(_config.WorkingDirectory))
                transportOptions.WorkingDirectory = _config.WorkingDirectory;

            if (_config.Env != null && _config.Env.Count > 0)
            {
                transportOptions.EnvironmentVariables = new Dictionary<string, string?>();
                foreach (var env in _config.Env)
                    transportOptions.EnvironmentVariables[env.Key] = env.Value;
            }

            _transport = new StdioClientTransport(transportOptions);
            _mcpClient = await McpClient.CreateAsync(_transport, cancellationToken: cancellationToken);

            _logger.LogInformation("MCP Server {Name} 连接成功", _config.Name);
        }

        public async Task DisconnectAsync()
        {
            if (_mcpClient != null)
            {
                try
                {
                    await _mcpClient.DisposeAsync();
                    _logger.LogInformation("MCP Server {Name} 已断开连接", _config.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "断开 MCP Server {Name} 连接时发生错误", _config.Name);
                }
                finally
                {
                    _mcpClient = null;
                    _transport = null;
                }
            }
        }

        public async Task<IReadOnlyList<Seeing.Agent.MCP.Management.McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default)
        {
            if (_mcpClient == null)
            {
                _logger.LogError("MCP 客户端未连接，无法获取工具列表");
                return new List<Seeing.Agent.MCP.Management.McpToolInfo>();
            }

            try
            {
                var tools = await _mcpClient.ListToolsAsync(cancellationToken: cancellationToken);
                var result = new List<Seeing.Agent.MCP.Management.McpToolInfo>();

                foreach (var tool in tools)
                {
                    result.Add(new Seeing.Agent.MCP.Management.McpToolInfo
                    {
                        Name = tool.Name,
                        Description = tool.Description,
                        ParametersSchema = tool.JsonSchema
                    });
                    _logger.LogDebug("发现 MCP 工具: {Name} - {Description}", tool.Name, tool.Description);
                }

                _logger.LogInformation("从 MCP Server {Name} 获取到 {Count} 个工具", _config.Name, result.Count);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取 MCP Server {Name} 工具列表失败", _config.Name);
                throw;
            }
        }

        public async Task<McpToolResult> CallToolAsync(string toolName, Dictionary<string, object?> args, CancellationToken cancellationToken = default)
        {
            if (_mcpClient == null)
            {
                _logger.LogError("MCP 客户端未连接，无法调用工具: {ToolName}", toolName);
                return new McpToolResult { IsError = true, Content = "MCP 客户端未连接" };
            }

            try
            {
                _logger.LogDebug("调用 MCP 工具: {ServerName}.{ToolName}", _config.Name, toolName);
                var result = await _mcpClient.CallToolAsync(toolName, args, cancellationToken: cancellationToken);
                return BuildToolResult(result, _config.Name, toolName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "调用 MCP 工具 {ServerName}.{ToolName} 失败", _config.Name, toolName);
                return new McpToolResult { IsError = true, Content = $"工具调用异常: {ex.Message}" };
            }
        }

        public static McpToolResult BuildToolResult(CallToolResult result, string serverName, string toolName)
        {
            var contentBuilder = new System.Text.StringBuilder();
            bool hasError = result.IsError ?? false;

            foreach (var content in result.Content)
            {
                if (content is TextContentBlock textBlock)
                    contentBuilder.AppendLine(textBlock.Text);
                else if (content is ImageContentBlock imageBlock)
                    contentBuilder.AppendLine($"[Image: {imageBlock.MimeType}]");
                else if (content is EmbeddedResourceBlock resourceBlock)
                    contentBuilder.AppendLine($"[Resource: {resourceBlock.Resource?.Uri}]");
                else
                    contentBuilder.AppendLine($"[Content: {content.Type}]");
            }

            return new McpToolResult { IsError = hasError, Content = contentBuilder.ToString().TrimEnd() };
        }
    }

    public class HttpMcpClientWrapper : IMcpClientWrapper
    {
        private readonly McpServerConfig _config;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger _logger;
        private readonly HttpTransportMode _transportMode;
        private McpClient? _mcpClient;
        private HttpClientTransport? _transport;

        public HttpMcpClientWrapper(
            McpServerConfig config,
            IHttpClientFactory httpClientFactory,
            ILogger logger,
            HttpTransportMode transportMode = HttpTransportMode.StreamableHttp)
        {
            _config = config;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _transportMode = transportMode;
        }

        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            if (_config.Url == null)
                throw new InvalidOperationException("HTTP 传输需要配置 endpoint");

            _logger.LogInformation("正在连接 MCP Server (HTTP): {Name}, Endpoint: {Endpoint}", _config.Name, _config.Url);

            var transportOptions = new HttpClientTransportOptions
            {
                Endpoint = _config.Url,
                TransportMode = _transportMode,
                ConnectionTimeout = _config.ConnectionTimeout,
                MaxReconnectionAttempts = _config.MaxReconnectionAttempts,
                DefaultReconnectionInterval = _config.ReconnectionInterval
            };

            if (_config.Headers != null && _config.Headers.Count > 0)
                transportOptions.AdditionalHeaders = new Dictionary<string, string>(_config.Headers);

            var httpClient = _httpClientFactory.CreateClient($"Mcp_{_config.Name}");
            _transport = new HttpClientTransport(transportOptions, httpClient, null, ownsHttpClient: false);
            _mcpClient = await McpClient.CreateAsync(_transport, cancellationToken: cancellationToken);

            _logger.LogInformation("MCP Server {Name} 连接成功 (Mode: {Mode})", _config.Name, _transportMode);
        }

        public async Task DisconnectAsync()
        {
            if (_mcpClient != null)
            {
                try
                {
                    await _mcpClient.DisposeAsync();
                    _logger.LogInformation("MCP Server {Name} 已断开连接", _config.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "断开 MCP Server {Name} 连接时发生错误", _config.Name);
                }
                finally
                {
                    _mcpClient = null;
                    _transport = null;
                }
            }
        }

        public async Task<IReadOnlyList<Seeing.Agent.MCP.Management.McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default)
        {
            if (_mcpClient == null)
            {
                _logger.LogError("MCP 客户端未连接，无法获取工具列表");
                return new List<Seeing.Agent.MCP.Management.McpToolInfo>();
            }

            try
            {
                var tools = await _mcpClient.ListToolsAsync(cancellationToken: cancellationToken);
                var result = new List<Seeing.Agent.MCP.Management.McpToolInfo>();

                foreach (var tool in tools)
                {
                    result.Add(new Seeing.Agent.MCP.Management.McpToolInfo
                    {
                        Name = tool.Name,
                        Description = tool.Description,
                        ParametersSchema = tool.JsonSchema
                    });
                }

                _logger.LogInformation("从 MCP Server {Name} 获取到 {Count} 个工具", _config.Name, result.Count);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取 MCP Server {Name} 工具列表失败", _config.Name);
                throw;
            }
        }

        public async Task<McpToolResult> CallToolAsync(string toolName, Dictionary<string, object?> args, CancellationToken cancellationToken = default)
        {
            if (_mcpClient == null)
            {
                _logger.LogError("MCP 客户端未连接，无法调用工具: {ToolName}", toolName);
                return new McpToolResult { IsError = true, Content = "MCP 客户端未连接" };
            }

            try
            {
                _logger.LogDebug("调用 MCP 工具: {ServerName}.{ToolName}", _config.Name, toolName);
                var result = await _mcpClient.CallToolAsync(toolName, args, cancellationToken: cancellationToken);
                return StdioMcpClientWrapper.BuildToolResult(result, _config.Name, toolName);
            }
            catch (HttpRequestException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "调用 MCP 工具 {ServerName}.{ToolName} 失败", _config.Name, toolName);
                return new McpToolResult { IsError = true, Content = $"工具调用异常: {ex.Message}" };
            }
        }
    }

    #endregion
}