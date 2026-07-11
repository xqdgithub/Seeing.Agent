using AntDesign;
using Seeing.Agent.WebUI.Models;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Seeing.Agent.WebUI.Commands
{
    /// <summary>
    /// 自定义命令加载器 - 从 Markdown 文件加载用户自定义命令
    /// </summary>
    public class CustomCommandLoader
    {
        private static readonly Regex FrontmatterRegex = new(
            @"^---\s*[\r]?\n(.*?)[\r]?\n---\s*[\r]?\n?",
            RegexOptions.Singleline | RegexOptions.Compiled);

        private readonly IDeserializer _yamlDeserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        /// <summary>
        /// 加载自定义命令（用户级 + 项目级）
        /// </summary>
        /// <param name="workspaceRoot">工作区根目录</param>
        /// <returns>自定义命令列表</returns>
        public async Task<List<CommandItemViewModel>> LoadAsync(string? workspaceRoot)
        {
            var commands = new Dictionary<string, CommandItemViewModel>(StringComparer.OrdinalIgnoreCase);

            // 1. 加载用户级命令 (~/.seeing/commands/)
            var userDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".seeing", "commands");
            await LoadFromDirectoryAsync(userDir, commands, "user", priority: 1);

            // 2. 加载项目级命令 (./.seeing/commands/) - 后加载，覆盖用户级同名
            if (!string.IsNullOrEmpty(workspaceRoot))
            {
                var projectDir = Path.Combine(workspaceRoot, ".seeing", "commands");
                await LoadFromDirectoryAsync(projectDir, commands, "project", priority: 0);
            }

            // 3. 检查与内置命令冲突
            foreach (var cmd in commands.Values)
            {
                if (BuiltInCommands.IsConflict(cmd.Name))
                {
                    cmd.IsDisabled = true;
                    cmd.DisabledReason = $"与内置命令 /{cmd.Name} 冲突";
                    cmd.Priority = 99; // 冲突命令排序最后
                }
            }

            return commands.Values.ToList();
        }

        private async Task LoadFromDirectoryAsync(
            string directory,
            Dictionary<string, CommandItemViewModel> commands,
            string source,
            int priority)
        {
            if (!Directory.Exists(directory))
                return;

            var files = Directory.GetFiles(directory, "*.md", SearchOption.AllDirectories);

            foreach (var file in files)
            {
                try
                {
                    var cmd = await ParseMarkdownAsync(file, source, priority);
                    if (cmd != null)
                    {
                        // 后加载覆盖同名命令
                        commands[cmd.Name] = cmd;
                    }
                }
                catch
                {
                    // 解析失败，跳过该文件
                }
            }
        }

        private async Task<CommandItemViewModel?> ParseMarkdownAsync(string filePath, string source, int priority)
        {
            var content = await File.ReadAllTextAsync(filePath);
            var match = FrontmatterRegex.Match(content);

            if (!match.Success)
                return null;

            var frontmatter = _yamlDeserializer.Deserialize<CommandFrontmatter>(match.Groups[1].Value);

            if (string.IsNullOrEmpty(frontmatter.Name))
                return null;

            var category = ParseCategory(frontmatter.Category);

            return new CommandItemViewModel
            {
                Value = $"/{frontmatter.Name}",
                Name = frontmatter.Name,
                Description = frontmatter.Description ?? "",
                IconType = IconType.Outline.Star,
                IsCustom = true,
                Category = category,
                Priority = priority,
                Source = source
            };
        }

        private static string ParseCategory(string? category) => category?.ToLowerInvariant() switch
        {
            "basic" => "Basic",
            "session" => "Session",
            "agent" => "Agent",
            "tools" => "Tools",
            "system" => "System",
            "view" => "View",
            _ => "Custom"
        };

        private class CommandFrontmatter
        {
            public string? Name { get; set; }
            public string? Description { get; set; }
            public string? Category { get; set; }
            public string[]? Aliases { get; set; }
        }
    }
}
