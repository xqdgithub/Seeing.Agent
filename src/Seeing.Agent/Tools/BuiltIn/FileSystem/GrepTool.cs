using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Abstractions;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Seeing.Agent.Tools.BuiltIn.FileSystem
{
    /// <summary>
    /// 内容搜索工具 - 在文件内容中搜索正则表达式模式
    /// </summary>
    public class GrepTool : ToolBase
    {
        private const int MaxLineLength = 2000;
        private const int DefaultLimit = 100;

        /// <summary>
        /// 创建 GrepTool 实例
        /// </summary>
        public GrepTool(ILogger<GrepTool> logger) : base(logger)
        {
        }

        public override string Id => "grep";

        public override string Description =>
            "在文件内容中搜索正则表达式模式。\n\n" +
            "支持在指定目录中递归搜索，可通过 include 参数过滤文件类型。\n" +
            "返回匹配的文件路径、行号和行内容。";

        public ToolCategory Category => ToolCategory.FileSystem;

        public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                pattern = new
                {
                    type = "string",
                    description = "要搜索的正则表达式模式"
                },
                path = new
                {
                    type = "string",
                    description = "搜索目录。默认使用当前工作目录。"
                },
                include = new
                {
                    type = "string",
                    description = "要包含的文件模式（如 '*.js', '*.{ts,tsx}'）"
                }
            },
            required = new[] { "pattern" }
        });

        public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
        {
            // 获取参数
            var pattern = GetStringArgument(arguments, "pattern");
            var searchPath = GetStringArgument(arguments, "path") ?? Directory.GetCurrentDirectory();
            var includePattern = GetStringArgument(arguments, "include");

            if (string.IsNullOrEmpty(pattern))
            {
                return Failure("pattern 参数是必需的");
            }

            // 确保路径是绝对路径
            if (!Path.IsPathRooted(searchPath))
            {
                searchPath = Path.GetFullPath(searchPath);
            }

            // 权限检查
            if (context.AskPermission != null)
            {
                await context.AskPermission(new PermissionRequest
                {
                    Permission = "grep",
                    Patterns = new List<string> { pattern },
                    Metadata = new Dictionary<string, object>
                    {
                        ["pattern"] = pattern,
                        ["path"] = searchPath,
                        ["include"] = includePattern ?? ""
                    }
                });
            }

            _logger.LogInformation("Grep 搜索: pattern={Pattern}, path={Path}, include={Include}",
                pattern, searchPath, includePattern);

            try
            {
                // 验证正则表达式
                Regex regex;
                try
                {
                    regex = new Regex(pattern, RegexOptions.Compiled);
                }
                catch (ArgumentException ex)
                {
                    return Failure($"无效的正则表达式: {ex.Message}");
                }

                // 检查目录是否存在
                if (!Directory.Exists(searchPath))
                {
                    return Failure($"目录不存在: {searchPath}");
                }

                // 执行搜索
                var matches = SearchDirectory(searchPath, regex, includePattern, DefaultLimit);

                // 构建输出
                var output = new List<string>();
                if (matches.Count == 0)
                {
                    output.Add("未找到匹配的文件");
                }
                else
                {
                    foreach (var match in matches)
                    {
                        var truncatedLine = match.LineText.Length > MaxLineLength
                            ? match.LineText.Substring(0, MaxLineLength) + "..."
                            : match.LineText;
                        output.Add($"{match.Path}:{match.LineNum}:{truncatedLine}");
                    }

                    if (matches.Count >= DefaultLimit)
                    {
                        output.Add("");
                        output.Add($"(显示前 {DefaultLimit} 条结果，可能还有更多匹配)");
                    }
                }

                return Success(
                    $"搜索: {pattern}",
                    string.Join("\n", output),
                    new Dictionary<string, object>
                    {
                        ["pattern"] = pattern,
                        ["path"] = searchPath,
                        ["matches"] = matches.Count,
                        ["truncated"] = matches.Count >= DefaultLimit
                    });
            }
            catch (UnauthorizedAccessException ex)
            {
                return Failure($"访问被拒绝: {ex.Message}");
            }
            catch (Exception ex)
            {
                return Failure(ex, "搜索失败");
            }
        }

        /// <summary>
        /// 在目录中搜索匹配的内容
        /// </summary>
        private List<GrepMatch> SearchDirectory(
            string directory,
            Regex regex,
            string? includePattern,
            int limit)
        {
            var matches = new List<GrepMatch>();

            SearchDirectoryRecursive(directory, regex, includePattern, matches, limit);

            // 按修改时间排序
            return matches
                .OrderByDescending(m => m.ModTime)
                .Take(limit)
                .ToList();
        }

        /// <summary>
        /// 递归搜索目录
        /// </summary>
        private void SearchDirectoryRecursive(
            string directory,
            Regex regex,
            string? includePattern,
            List<GrepMatch> matches,
            int limit)
        {
            if (matches.Count >= limit) return;

            try
            {
                var dirInfo = new DirectoryInfo(directory);

                // 搜索文件
                foreach (var file in dirInfo.GetFiles())
                {
                    if (matches.Count >= limit) break;

                    // 检查 include 模式
                    if (!string.IsNullOrEmpty(includePattern) &&
                        !FileSystemHelper.MatchesGlobPattern(file.FullName, includePattern))
                    {
                        continue;
                    }

                    // 跳过二进制文件
                    if (FileSystemHelper.IsBinaryByExtension(file.FullName) ||
                        FileSystemHelper.IsBinaryByContent(file.FullName))
                    {
                        continue;
                    }

                    // 搜索文件内容
                    SearchFileContent(file.FullName, regex, matches, limit);
                }

                // 递归搜索子目录
                foreach (var subDir in dirInfo.GetDirectories())
                {
                    if (matches.Count >= limit) break;

                    // 跳过隐藏目录
                    if (subDir.Name.StartsWith(".")) continue;

                    SearchDirectoryRecursive(subDir.FullName, regex, includePattern, matches, limit);
                }
            }
            catch (UnauthorizedAccessException)
            {
                // 跳过无权限目录
            }
            catch (DirectoryNotFoundException)
            {
                // 跳过不存在的目录
            }
        }

        /// <summary>
        /// 在单个文件中搜索内容
        /// </summary>
        private void SearchFileContent(
            string filePath,
            Regex regex,
            List<GrepMatch> matches,
            int limit)
        {
            try
            {
                var fileInfo = new FileInfo(filePath);
                var modTime = fileInfo.LastWriteTimeUtc.Ticks;

                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
                using var reader = new StreamReader(stream, Encoding.UTF8);

                var lineNum = 0;
                while (true)
                {
                    var line = reader.ReadLine();
                    if (line == null) break;

                    lineNum++;

                    if (matches.Count >= limit) break;

                    // 检查是否匹配
                    if (regex.IsMatch(line))
                    {
                        matches.Add(new GrepMatch
                        {
                            Path = filePath,
                            LineNum = lineNum,
                            LineText = line.Length > MaxLineLength
                                ? line.Substring(0, MaxLineLength) + "..."
                                : line,
                            ModTime = modTime
                        });
                    }
                }
            }
            catch
            {
                // 文件读取失败，跳过
            }
        }
    }
}
