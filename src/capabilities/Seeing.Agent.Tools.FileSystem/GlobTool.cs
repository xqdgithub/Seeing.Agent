using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Tools.Support;
using System.Text.Json;

namespace Seeing.Agent.Tools.FileSystem
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
        private readonly IExecutionWorld _world;
        private readonly IWorkspacePathGate _pathGate;

        /// <summary>
        /// 创建 GlobTool 实例
        /// </summary>
        public GlobTool(
            ILogger<GlobTool> logger,
            IExecutionWorld world,
            IWorkspacePathGate pathGate) : base(logger)
        {
            _world = world;
            _pathGate = pathGate;
        }

        public override string Id => "glob";

        public override string Description =>
            "按文件名或路径模式查找文件；按内容搜索用 `grep`。\n\n" +
            "- 模式：支持 * 与 **，如 **/*.cs、src/**/*.ts。\n" +
            "- 结果：按修改时间倒序，默认最多 100 个；被截断时会提示。\n" +
            "- 范围：path 缺省为当前工作目录。";

        public override ToolCategory Category => ToolCategory.FileSystem;

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
                        description = "用于匹配文件的 glob 模式（支持 * 与 **，如 **/*.cs）"
                    },
                    path = new
                    {
                        type = "string",
                        description = "搜索的目录：绝对路径，或相对当前工作目录的路径（默认当前工作目录）"
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
                // 使用执行世界当前工作目录
                searchPath = _world.Cwd;
            }

            // 确保路径是绝对路径
            if (!Path.IsPathRooted(searchPath))
            {
                searchPath = _world.FileSystem.GetFullPath(searchPath);
            }

            var denied = PathGateHelper.RejectIfDenied(_pathGate, context, searchPath, Failure);
            if (denied != null)
                return denied;

            // 检查目录是否存在
            if (!_world.FileSystem.Exists(searchPath))
            {
                return Failure($"目录不存在: {searchPath}");
            }

            _logger.LogInformation("Glob 搜索: pattern={Pattern}, path={Path}", pattern, searchPath);

            // Prefer ripgrep --files via ISubprocess when available; else managed glob walk.
            var files = await RipgrepSearch.GlobAsync(_world, searchPath, pattern, DefaultLimit, context.CancellationToken, _logger);

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
