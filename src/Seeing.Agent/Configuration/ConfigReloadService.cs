using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Seeing.Agent.Configuration
{
    /// <summary>
    /// 配置热重载服务 - 监听配置变更并触发更新
    /// </summary>
    public interface IConfigReloadService
    {
        /// <summary>配置变更事件</summary>
        event EventHandler<ConfigReloadEventArgs>? ConfigChanged;
        
        /// <summary>启动监听</summary>
        Task StartAsync(CancellationToken cancellationToken = default);
        
        /// <summary>停止监听</summary>
        Task StopAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// 配置变更事件参数
    /// </summary>
    public class ConfigReloadEventArgs : EventArgs
    {
        /// <summary>变更类型</summary>
        public ConfigChangeType ChangeType { get; set; }
        
        /// <summary>变更的配置节</summary>
        public string? Section { get; set; }
        
        /// <summary>新的设置值</summary>
        public RuntimeSettings? NewSettings { get; set; }
        
        /// <summary>变更时间</summary>
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// 配置变更类型
    /// </summary>
    public enum ConfigChangeType
    {
        /// <summary>默认代理变更</summary>
        DefaultAgentChanged,
        
        /// <summary>Agent 模型变更</summary>
        AgentModelChanged,
        
        /// <summary>设置重置</summary>
        SettingsReset,
        
        /// <summary>文件变更</summary>
        FileChanged
    }

    /// <summary>
    /// 配置热重载服务实现
    /// </summary>
    public class ConfigReloadService : IConfigReloadService, IDisposable
    {
        private readonly ILogger<ConfigReloadService> _logger;
        private readonly IConfigurationPersistence _persistence;
        private readonly FileSystemWatcher? _watcher;
        private RuntimeSettings? _currentSettings;
        private readonly object _lock = new();

        /// <summary>配置变更事件</summary>
        public event EventHandler<ConfigReloadEventArgs>? ConfigChanged;

        /// <summary>
        /// 创建配置热重载服务
        /// </summary>
        public ConfigReloadService(
            ILogger<ConfigReloadService> logger,
            IConfigurationPersistence persistence)
        {
            _logger = logger;
            _persistence = persistence;

            // 设置文件监听
            var directory = Path.GetDirectoryName(_persistence.SettingsFilePath);
            var fileName = Path.GetFileName(_persistence.SettingsFilePath);
            
            if (!string.IsNullOrEmpty(directory) && !string.IsNullOrEmpty(fileName))
            {
                _watcher = new FileSystemWatcher(directory, fileName)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = false
                };
                
                _watcher.Changed += OnFileChanged;
            }
        }

        /// <inheritdoc/>
        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            // 加载当前设置
            _currentSettings = await _persistence.LoadAsync(cancellationToken);
            
            // 启动文件监听
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = true;
                _logger.LogInformation("配置热重载服务已启动，监听: {Path}", _persistence.SettingsFilePath);
            }
        }

        /// <inheritdoc/>
        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _logger.LogInformation("配置热重载服务已停止");
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// 文件变更处理
        /// </summary>
        private async void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            try
            {
                // 防抖：等待文件写入完成
                await Task.Delay(100);
                
                var newSettings = await _persistence.LoadAsync();
                
                lock (_lock)
                {
                    var changes = DetectChanges(_currentSettings, newSettings);
                    _currentSettings = newSettings;
                    
                    foreach (var change in changes)
                    {
                        OnConfigChanged(change);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理配置文件变更失败");
            }
        }

        /// <summary>
        /// 检测配置变更
        /// </summary>
        private List<ConfigReloadEventArgs> DetectChanges(RuntimeSettings? oldSettings, RuntimeSettings newSettings)
        {
            var changes = new List<ConfigReloadEventArgs>();
            
            // 默认代理变更
            if (oldSettings?.DefaultAgent != newSettings.DefaultAgent)
            {
                changes.Add(new ConfigReloadEventArgs
                {
                    ChangeType = ConfigChangeType.DefaultAgentChanged,
                    Section = "DefaultAgent",
                    NewSettings = newSettings
                });
            }

            // Agent 模型变更
            if (oldSettings?.AgentModels != null && newSettings.AgentModels != null)
            {
                foreach (var (agent, model) in newSettings.AgentModels)
                {
                    if (!oldSettings.AgentModels.TryGetValue(agent, out var oldModel) || oldModel != model)
                    {
                        changes.Add(new ConfigReloadEventArgs
                        {
                            ChangeType = ConfigChangeType.AgentModelChanged,
                            Section = $"AgentModels.{agent}",
                            NewSettings = newSettings
                        });
                    }
                }
            }

            return changes;
        }

        /// <summary>
        /// 触发配置变更事件
        /// </summary>
        protected virtual void OnConfigChanged(ConfigReloadEventArgs e)
        {
            _logger.LogInformation("检测到配置变更: {Type}, Section: {Section}", e.ChangeType, e.Section);
            ConfigChanged?.Invoke(this, e);
        }

        /// <summary>
        /// 手动触发重载
        /// </summary>
        public async Task ReloadAsync(CancellationToken cancellationToken = default)
        {
            var newSettings = await _persistence.LoadAsync(cancellationToken);
            
            lock (_lock)
            {
                _currentSettings = newSettings;
            }
            
            OnConfigChanged(new ConfigReloadEventArgs
            {
                ChangeType = ConfigChangeType.FileChanged,
                NewSettings = newSettings
            });
        }

        public void Dispose()
        {
            _watcher?.Dispose();
        }
    }
}