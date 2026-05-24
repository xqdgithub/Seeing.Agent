using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Configuration;
using System.Text.Json;

namespace Seeing.Agent.Memory.Configuration
{
    /// <summary>
    /// Memory 配置加载器：按用户级 -> 项目级的顺序加载并合并 Memory 配置。
    /// </summary>
    public static class MemoryConfigLoader
    {
        /// <summary>
        /// 用户主目录下的 Memory 配置路径（带 ~ 的相对表达）。
        /// 例如 "~/.seeing/memory.json"。
        /// </summary>
        public static string UserMemoryJsonPath => Path.Combine("~", ".seeing", "memory.json").Replace('\\', '/');

        /// <summary>
        /// 项目工作区下的 Memory 配置路径（相对路径）。
        /// 例如 "./.seeing/memory.json"。
        /// </summary>
        public static string ProjectMemoryJsonPath(string workspaceRoot) => Path.Combine(".", ".seeing", "memory.json").Replace('\\', '/');

        /// <summary>
        /// 加载并合并 Memory 配置：优先使用用户级配置，若存在项目级配置则覆盖同名字段。
        /// 未找到配置时返回一个默认的 MemoryOptions。
        /// </summary>
        public static MemoryOptions LoadDefault(string workspaceRoot, ILogger? logger = null)
        {
            var userPathRel = UserMemoryJsonPath;
            var projectPathRel = ProjectMemoryJsonPath(workspaceRoot);

            var userPath = ExpandPath(userPathRel, workspaceRoot);
            var projectPath = ExpandPath(projectPathRel, workspaceRoot);

            MemoryOptions? userOptions = null;
            MemoryOptions? projectOptions = null;
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            try
            {
                if (File.Exists(userPath))
                {
                    var json = File.ReadAllText(userPath);
                    userOptions = JsonSerializer.Deserialize<MemoryOptions>(json, options);
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to load user memory config at {Path}", userPath);
            }

            try
            {
                if (File.Exists(projectPath))
                {
                    var json = File.ReadAllText(projectPath);
                    projectOptions = JsonSerializer.Deserialize<MemoryOptions>(json, options);
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to load project memory config at {Path}", projectPath);
            }

            var baseOptions = userOptions ?? new MemoryOptions();
            var merged = projectOptions == null ? baseOptions : MergeDeep.Merge(baseOptions, projectOptions);
            return merged ?? new MemoryOptions();
        }

        /// <summary>
        /// 将路径中的 ~ 展开为用户主目录，或者将相对路径展开为工作区路径
        /// </summary>
        public static string ExpandPath(string path, string workspaceRoot)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;
            // 处理 ~ 开头
            if (path.StartsWith("~"))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var tail = path.Substring(1).TrimStart('/', '\\');
                return Path.Combine(home, tail);
            }
            // 处理相对路径
            if (!Path.IsPathRooted(path))
            {
                return Path.GetFullPath(Path.Combine(workspaceRoot, path));
            }
            return path;
        }
    }
}
