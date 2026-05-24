using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Abstractions;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using System.Text.Json;

namespace Seeing.Agent.Tools.BuiltIn.FileSystem
{
    /// <summary>
    /// 文件模式匹配工具
    /// <para>
    /// 使用 glob 模式快速匹配文件。支持 * 和 ** 通配符。
    /// 返回按修改时间排序的文件列表。
    /// </para>
    /// </summary>
    public class GlobTool : ToolBase
    {
        /// <summary>
        /// 默认结果限制
        /// </summary>
        private const int DefaultLimit = 100;

        /// <summary>
        /// 创建 GlobTool 实例
        /// </summary>
        public GlobTool(ILogger<GlobTool> logger) : base(logger)
        {
        }

        public override string Id => "glob";

        public override string Description =>
            "使用 glob 模式快速匹配文件。\n\n" +
            "支持 glob 模式匹配，如 \"**/*.js\" 或 \"src/**/*.ts\"。\n" +
            "返回匹配的文件路径列表，按修改时间排序（最近的在前）。\n" +
            "默认限制返回 100 个结果，可通过更具体的模式或路径来获取更多结果。";

        public ToolCategory Category => ToolCategory.FileSystem;

        public override JsonElement ParametersSchema => BuildParametersSchema();

        private JsonElement BuildParametersSchema()
        {
            var schema = new
            {
                type = "object",
                properties = new
                {
                    pattern = new
                    {
                        type = "string",
                        description = "用于匹配文件的 glob 模式"
                    },
                    path = new
                    {
                        type = "string",
                        description = "搜索的目录路径。如果未指定，使用当前工作目录。必须是有效的目录路径。"
                    }
                },
                required = new[] { "pattern" }
            };

            return JsonSerializer.SerializeToElement(schema);
        }

        public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
        {
            // 获取 pattern 参数
            var pattern = GetStringArgument(arguments, "pattern");
            if (string.IsNullOrEmpty(pattern))
            {
                return Failure("缺少必需参数: pattern");
            }

            // 获取 path 参数
            var searchPath = GetStringArgument(arguments, "path");
            if (string.IsNullOrEmpty(searchPath))
            {
                // 使用当前工作目录
                searchPath = Directory.GetCurrentDirectory();
            }

            // 确保路径是绝对路径
            if (!Path.IsPathRooted(searchPath))
            {
                searchPath = Path.GetFullPath(searchPath);
            }

            // 检查目录是否存在
            if (!Directory.Exists(searchPath))
            {
                return Failure($"目录不存在: {searchPath}");
            }

            _logger.LogInformation("Glob 搜索: pattern={Pattern}, path={Path}", pattern, searchPath);

            // 权限检查
            if (context.AskPermission != null)
            {
                await context.AskPermission(new PermissionRequest
                {
                    Permission = "glob",
                    Patterns = new List<string> { pattern },
                    Metadata = new Dictionary<string, object>
                    {
                        ["pattern"] = pattern,
                        ["path"] = searchPath
                    }
                });
            }

            // 执行搜索
            var files = FileSystemHelper.GlobSearch(searchPath, pattern, DefaultLimit);

            // 构建输出
            var outputLines = new List<string>();

            if (files.Count == 0)
            {
                outputLines.Add("未找到匹配文件");
            }
            else
            {
                outputLines.AddRange(files);

                // 检查是否有更多结果
                var truncated = files.Count >= DefaultLimit;
                if (truncated)
                {
                    outputLines.Add("");
                    outputLines.Add($"(结果已截断: 显示前 {DefaultLimit} 个结果。考虑使用更具体的路径或模式。)");
                }
            }

            return Success(
                Path.GetFileName(searchPath) ?? searchPath,
                string.Join("\n", outputLines),
                new Dictionary<string, object>
                {
                    ["count"] = files.Count,
                    ["pattern"] = pattern,
                    ["path"] = searchPath,
                    ["truncated"] = files.Count >= DefaultLimit
                }
            );
        }
    }
}
