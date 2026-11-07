using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Tools.Support;
using System.Text.Json;

namespace Seeing.Agent.Tools.FileSystem
{
    /// <summary>
    /// 内容搜索工具 - 在文件内容中搜索正则表达式模式
    /// </summary>
    [ToolCapability(ToolCapabilityKeys.TimeoutBudget, "60000")]
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
            "按正则搜索文件内容，返回路径与行号；按文件名找文件用 `glob`。\n\n" +
            "- 范围：默认递归搜索当前工作目录；path 只能是目录。\n" +
            "- 过滤：include 支持 '*.cs'、'*.{ts,tsx}' 等模式。\n" +
            "- 结果：默认最多 100 条；pattern 是 .NET 正则，语法错误会直接报错。";

        public override ToolCategory Category => ToolCategory.FileSystem;

        public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                pattern = new
                {
                    type = "string",
                    description = "要搜索的正则表达式（.NET 语法）"
                },
                path = new
                {
                    type = "string",
                    description = "搜索目录：绝对路径，或相对当前工作目录的路径（默认当前工作目录；不支持传单个文件）"
                },
                include = new
                {
                    type = "string",
                    description = "要包含的文件模式（如 '*.js', '*.{ts,tsx}'）"
                },
                hidden = new
                {
                    type = "boolean",
                    description = "是否搜索隐藏项（点前缀文件/目录，如 .github、.env）。默认 false；"
                                + "开启后也会进入 .git、.venv、node_modules 等目录，慎用于大仓库"
                }
            },
            required = new[] { "pattern" }
        });

        public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
        {
            var pattern = GetStringArgument(arguments, "pattern");
            var searchPath = GetStringArgument(arguments, "path") ?? _world.Cwd;
            var includePattern = GetStringArgument(arguments, "include");
            var includeHidden = GetBoolArgument(arguments, "hidden") ?? false;

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
                if (!_world.FileSystem.Exists(searchPath))
                {
                    return Failure($"目录不存在: {searchPath}");
                }

                // 正则预校验只在托管分支做：rg 用 Rust 正则，.NET 语法校验对它是错的编译器。
                // 托管回退时 FileSystemHelper.GrepSearch 会自行构造 Regex 并在失败时抛错。
                var outcome = await RipgrepSearch.GrepAsync(
                    _world,
                    searchPath,
                    pattern,
                    includePattern,
                    DefaultLimit,
                    context.CancellationToken,
                    _logger,
                    includeHidden);

                // 超时不回退：明确告知而不是谎报"未找到"
                if (outcome.TimedOut)
                {
                    return Failure($"搜索超时（超过 {RipgrepSearch.TimeoutDisplay}），请缩小 path 或使用更具体的模式");
                }

                // 全局截断在 C# 侧判定（rg 的 --max-count 是每文件上限）
                var matches = outcome.Items.Take(DefaultLimit).ToList();
                var truncated = outcome.Items.Count > DefaultLimit;

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

                    if (truncated)
                    {
                        output.Add("");
                        output.Add($"(显示前 {DefaultLimit} 条结果，可能还有更多匹配)");
                    }
                }

                if (outcome.Fallback is not null)
                {
                    output.Add("");
                    output.Add($"(ripgrep 不可用，已回退托管搜索: {outcome.Fallback.Reason})");
                }

                var metadata = new Dictionary<string, object>
                {
                    ["pattern"] = pattern,
                    ["path"] = searchPath,
                    ["matches"] = matches.Count,
                    ["truncated"] = truncated,
                    ["backend"] = outcome.Backend
                };

                if (outcome.Fallback is not null)
                    metadata["diagnostic"] = outcome.Fallback.Reason;

                return Success($"搜索: {pattern}", string.Join("\n", output), metadata);
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
