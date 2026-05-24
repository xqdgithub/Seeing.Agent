using Microsoft.Extensions.Logging;
using Seeing.Agent.Configuration;
using System.Text.Json;

namespace Seeing.Agent.Extensions
{
    /// <summary>
    /// 扩展配置加载器 - 支持 用户级 + 项目级 分层配置
    /// <para>
    /// 参考 SeeingMcpConfigLoader 设计，配置路径：
    /// - 用户级：~/.seeing/seeing.json
    /// - 项目级：&lt;workspace&gt;/.seeing/seeing.json
    /// </para>
    /// </summary>
    public static class ExtensionConfigLoader
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        /// <summary>
        /// 加载扩展配置（合并用户级 + 项目级）
        /// </summary>
        /// <param name="userPath">用户级配置路径</param>
        /// <param name="projectPath">项目级配置路径</param>
        /// <param name="logger">日志器</param>
        /// <returns>插件规格列表</returns>
        public static IReadOnlyList<PluginSpec> Load(
            string? userPath,
            string? projectPath,
            ILogger? logger = null)
        {
            var specs = new Dictionary<string, PluginSpec>(StringComparer.OrdinalIgnoreCase);

            // 先加载用户级
            LoadFromFile(userPath, specs, logger, "用户级");

            // 后加载项目级（覆盖同名）
            LoadFromFile(projectPath, specs, logger, "项目级");

            return specs.Values.ToList().AsReadOnly();
        }

        /// <summary>
        /// 从文件加载配置
        /// </summary>
        private static void LoadFromFile(
            string? path,
            Dictionary<string, PluginSpec> specs,
            ILogger? logger,
            string levelLabel)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return;

            try
            {
                var json = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });

                var root = doc.RootElement;

                // 尝试从 SeeingAgent.Plugins 读取
                if (root.TryGetProperty("SeeingAgent", out var seeingAgent) &&
                    seeingAgent.TryGetProperty("Plugins", out var plugins))
                {
                    ParsePluginsArray(plugins, specs, logger, levelLabel);
                }
                // 或者从顶层 plugins 读取
                else if (root.TryGetProperty("plugins", out var topLevelPlugins))
                {
                    ParsePluginsArray(topLevelPlugins, specs, logger, levelLabel);
                }
            }
            catch (JsonException ex)
            {
                logger?.LogWarning(ex, "{Level} extension config parse failed: {Path}", levelLabel, path);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "{Level} extension config read failed: {Path}", levelLabel, path);
            }
        }

        /// <summary>
        /// 解析 plugins 数组
        /// </summary>
        private static void ParsePluginsArray(
            JsonElement plugins,
            Dictionary<string, PluginSpec> specs,
            ILogger? logger,
            string levelLabel)
        {
            if (plugins.ValueKind != JsonValueKind.Array)
                return;

            foreach (var item in plugins.EnumerateArray())
            {
                var spec = ParsePluginSpec(item);
                if (spec != null && !string.IsNullOrEmpty(spec.Spec))
                {
                    var key = ExtractKey(spec.Spec);
                    specs[key] = spec;
                    logger?.LogDebug("Loaded {Level} extension: {Spec}", levelLabel, spec.Spec);
                }
            }
        }

        /// <summary>
        /// 解析单个 PluginSpec
        /// </summary>
        private static PluginSpec? ParsePluginSpec(JsonElement element)
        {
            // 字符串格式："@seeing/analytics@1.0.0"
            if (element.ValueKind == JsonValueKind.String)
            {
                return new PluginSpec { Spec = element.GetString() ?? "" };
            }

            // 数组格式：["@seeing/analytics", { "option": "value" }]
            if (element.ValueKind == JsonValueKind.Array)
            {
                var items = element.EnumerateArray().ToArray();
                if (items.Length >= 1 && items[0].ValueKind == JsonValueKind.String)
                {
                    var spec = new PluginSpec
                    {
                        Spec = items[0].GetString() ?? ""
                    };

                    if (items.Length >= 2 && items[1].ValueKind == JsonValueKind.Object)
                    {
                        try
                        {
                            spec.Options = JsonSerializer.Deserialize<Dictionary<string, object>>(
                                items[1].GetRawText(), JsonOptions);
                        }
                        catch
                        {
                            // 忽略选项解析错误
                        }
                    }

                    return spec;
                }
            }

            return null;
        }

        /// <summary>
        /// 从 spec 提取 key（去掉版本号）
        /// </summary>
        private static string ExtractKey(string spec)
        {
            // 文件路径使用完整路径作为 key
            if (spec.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ||
                spec.StartsWith("./", StringComparison.Ordinal) ||
                spec.StartsWith("../", StringComparison.Ordinal) ||
                spec.StartsWith("~", StringComparison.Ordinal) ||
                Path.IsPathRooted(spec))
            {
                return spec;
            }

            // NuGet 包名去掉版本号
            var atCount = spec.Count(c => c == '@');
            if (spec.StartsWith("@", StringComparison.Ordinal) && atCount >= 2)
            {
                var secondAt = spec.IndexOf('@', 1);
                return spec.Substring(0, secondAt);
            }

            var lastAt = spec.LastIndexOf('@');
            if (lastAt > 0)
            {
                return spec.Substring(0, lastAt);
            }

            return spec;
        }

        /// <summary>
        /// 获取默认配置路径
        /// </summary>
        /// <param name="workspaceRoot">工作区根目录</param>
        /// <returns>(用户级路径, 项目级路径)</returns>
        public static (string? UserPath, string? ProjectPath) GetDefaultPaths(string workspaceRoot)
        {
            var user = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".seeing", "seeing.json");

            var project = Path.Combine(workspaceRoot, ".seeing", "seeing.json");

            return (File.Exists(user) ? user : null, File.Exists(project) ? project : null);
        }

        /// <summary>
        /// 加载默认配置
        /// </summary>
        /// <param name="workspaceRoot">工作区根目录</param>
        /// <param name="logger">日志器</param>
        /// <returns>插件规格列表</returns>
        public static IReadOnlyList<PluginSpec> LoadDefault(string workspaceRoot, ILogger? logger = null)
        {
            var (userPath, projectPath) = GetDefaultPaths(workspaceRoot);
            return Load(userPath, projectPath, logger);
        }

        /// <summary>
        /// 加载启用状态覆盖
        /// </summary>
        /// <param name="userPath">用户级配置路径</param>
        /// <param name="projectPath">项目级配置路径</param>
        /// <param name="logger">日志器</param>
        /// <returns>启用状态字典</returns>
        public static Dictionary<string, bool> LoadEnabledOverrides(
            string? userPath,
            string? projectPath,
            ILogger? logger = null)
        {
            var result = new Dictionary<string, bool>();

            LoadEnabledFromFile(userPath, result, logger, "用户级");
            LoadEnabledFromFile(projectPath, result, logger, "项目级");

            return result;
        }

        private static void LoadEnabledFromFile(
            string? path,
            Dictionary<string, bool> result,
            ILogger? logger,
            string levelLabel)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return;

            try
            {
                var json = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });

                var root = doc.RootElement;

                // 尝试从 SeeingAgent.PluginEnabled 读取
                if (root.TryGetProperty("SeeingAgent", out var seeingAgent) &&
                    seeingAgent.TryGetProperty("PluginEnabled", out var enabled))
                {
                    ParseEnabledMap(enabled, result, logger, levelLabel);
                }
                // 或者从顶层 plugin_enabled 读取
                else if (root.TryGetProperty("plugin_enabled", out var topLevelEnabled))
                {
                    ParseEnabledMap(topLevelEnabled, result, logger, levelLabel);
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "{Level} plugin_enabled parse failed: {Path}", levelLabel, path);
            }
        }

        private static void ParseEnabledMap(
            JsonElement enabled,
            Dictionary<string, bool> result,
            ILogger? logger,
            string levelLabel)
        {
            if (enabled.ValueKind != JsonValueKind.Object)
                return;

            foreach (var prop in enabled.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.True || prop.Value.ValueKind == JsonValueKind.False)
                {
                    result[prop.Name] = prop.Value.GetBoolean();
                    logger?.LogDebug("{Level} plugin_enabled: {Id} = {Value}",
                        levelLabel, prop.Name, prop.Value.GetBoolean());
                }
            }
        }

        /// <summary>
        /// 加载默认启用状态覆盖
        /// </summary>
        public static Dictionary<string, bool> LoadDefaultEnabledOverrides(
            string workspaceRoot,
            ILogger? logger = null)
        {
            var (userPath, projectPath) = GetDefaultPaths(workspaceRoot);
            return LoadEnabledOverrides(userPath, projectPath, logger);
        }
    }
}