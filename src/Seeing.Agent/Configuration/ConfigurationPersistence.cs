using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Seeing.Agent.Configuration
{
    /// <summary>
    /// 配置持久化服务 - 将运行时设置保存到 .seeing/settings.json
    /// </summary>
    public class ConfigurationPersistence : IConfigurationPersistence
    {
        private readonly ILogger<ConfigurationPersistence> _logger;
        private readonly string _settingsFilePath;
        private readonly JsonSerializerOptions _jsonOptions;

        /// <summary>
        /// 创建配置持久化实例
        /// </summary>
        /// <param name="logger">日志器</param>
        /// <param name="basePath">配置目录路径，默认为当前工作目录下的 .seeing</param>
        public ConfigurationPersistence(
            ILogger<ConfigurationPersistence> logger,
            string? basePath = null)
        {
            _logger = logger;
            var baseDir = basePath ?? Path.Combine(Directory.GetCurrentDirectory(), ".seeing");
            _settingsFilePath = Path.Combine(baseDir, "settings.json");

            _jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };
        }

        /// <inheritdoc/>
        public string SettingsFilePath => _settingsFilePath;

        /// <inheritdoc/>
        public async Task<RuntimeSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                if (!File.Exists(_settingsFilePath))
                {
                    _logger.LogDebug("设置文件不存在，返回默认设置: {Path}", _settingsFilePath);
                    return new RuntimeSettings();
                }

                var json = await File.ReadAllTextAsync(_settingsFilePath, cancellationToken);
                var settings = JsonSerializer.Deserialize<RuntimeSettings>(json, _jsonOptions);

                if (settings == null)
                {
                    _logger.LogWarning("设置文件反序列化为 null，返回默认设置");
                    return new RuntimeSettings();
                }

                _logger.LogDebug("已加载运行时设置: DefaultAgent={Agent}, DefaultModel={Model}",
                    settings.DefaultAgent, settings.DefaultModel);

                return settings;
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "设置文件格式错误，返回默认设置: {Path}", _settingsFilePath);
                return new RuntimeSettings();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载设置文件失败，返回默认设置: {Path}", _settingsFilePath);
                return new RuntimeSettings();
            }
        }

        /// <inheritdoc/>
        public async Task SaveAsync(RuntimeSettings settings, CancellationToken cancellationToken = default)
        {
            try
            {
                // 确保目录存在
                var directory = Path.GetDirectoryName(_settingsFilePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                    _logger.LogDebug("已创建配置目录: {Directory}", directory);
                }

                settings.UpdatedAt = DateTime.Now;
                var json = JsonSerializer.Serialize(settings, _jsonOptions);

                await File.WriteAllTextAsync(_settingsFilePath, json, cancellationToken);

                _logger.LogInformation("已保存运行时设置: DefaultAgent={Agent}, DefaultModel={Model}, Path={Path}",
                    settings.DefaultAgent, settings.DefaultModel, _settingsFilePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存设置文件失败: {Path}", _settingsFilePath);
                throw;
            }
        }

        /// <inheritdoc/>
        public async Task ResetAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                if (File.Exists(_settingsFilePath))
                {
                    File.Delete(_settingsFilePath);
                    _logger.LogInformation("已删除设置文件: {Path}", _settingsFilePath);
                }

                // 保存空设置
                await SaveAsync(new RuntimeSettings(), cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "重置设置失败: {Path}", _settingsFilePath);
                throw;
            }
        }
    }
}