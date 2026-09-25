using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Tools.Support;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Seeing.Agent.Tools.FileSystem
{
    /// <summary>
    /// 内容搜索工具 - 在文件内容中搜索正则表达式模式
    /// </summary>
    public class GrepTool : ToolBase
    {
        private const int DefaultLimit = 100;
        private readonly IExecutionWorld _world;
        private readonly IWorkspacePathGate _pathGate;

        /// <summary>
        /// 创建 GrepTool 实例
        /// </summary>
        public GrepTool(
            ILogger<GrepTool> logger,
            IExecutionWorld world,
            IWorkspacePathGate pathGate) : base(logger)
        {
            _world = world;
            _pathGate = pathGate;
        }

        public override string Id => "grep";

        public override string Description =>
            "在文件内容中搜索正则表达式模式。\n\n" +
            "支持在指定目录中递归搜索，可通过 include 参数过滤文件类型。\n" +
            "返回匹配的文件路径、行号和行内容。";

        public override ToolCategory Category => ToolCategory.FileSystem;

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
            var pattern = GetStringArgument(arguments, "pattern");
            var searchPath = GetStringArgument(arguments, "path") ?? _world.Cwd;
            var includePattern = GetStringArgument(arguments, "include");

            if (string.IsNullOrEmpty(pattern))
            {
                return Failure("pattern 参数是必需的");
            }

            if (!Path.IsPathRooted(searchPath))
            {
                searchPath = _world.FileSystem.GetFullPath(searchPath);
            }

            var denied = PathGateHelper.RejectIfDenied(_pathGate, context, searchPath, Failure);
            if (denied != null)
                return denied;

            _logger.LogInformation("Grep 搜索: pattern={Pattern}, path={Path}, include={Include}",
                pattern, searchPath, includePattern);

            try
            {
                try
                {
                    _ = new Regex(pattern, RegexOptions.Compiled, FileSystemHelper.RegexTimeout);
                }
                catch (ArgumentException ex)
                {
                    return Failure($"无效的正则表达式: {ex.Message}");
                }

                if (!_world.FileSystem.Exists(searchPath))
                {
                    return Failure($"目录不存在: {searchPath}");
                }

                // Prefer ripgrep via ISubprocess when available; else managed walk + IFileSystem.
                var matches = await RipgrepSearch.GrepAsync(
                    _world,
                    searchPath,
                    pattern,
                    includePattern,
                    DefaultLimit,
                    context.CancellationToken,
                    _logger);

                var output = new List<string>();
                if (matches.Count == 0)
                {
                    output.Add("未找到匹配的文件");
                }
                else
                {
                    foreach (var match in matches)
                    {
                        var truncatedLine = match.LineText.Length > FileSystemHelper.MaxLineLength
                            ? match.LineText.Substring(0, FileSystemHelper.MaxLineLength) + "..."
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
    }
}
