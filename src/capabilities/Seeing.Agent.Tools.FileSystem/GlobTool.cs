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
    [ToolCapability(ToolCapabilityKeys.TimeoutBudget, "60000")]
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
                    },
                    hidden = new
                    {
                        type = "boolean",
                        description = "是否搜索隐藏项（点前缀文件/目录，如 .github、.env）。默认 false；"
                                    + "开启后也会进入 .git、.venv、node_modules 等目录，慎用于大仓库"
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
            var includeHidden = GetBoolArgument(arguments, "hidden") ?? false;
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
            var outcome = await RipgrepSearch.GlobAsync(_world, searchPath, pattern, DefaultLimit, context.CancellationToken, _logger, includeHidden);

            // 超时不回退：明确告知而不是谎报"未找到"
            if (outcome.TimedOut)
            {
                return Failure($"搜索超时（超过 {RipgrepSearch.TimeoutDisplay}），请缩小 path 或使用更具体的模式");
            }

            // 全局截断在 C# 侧判定（rg --files 无全局上限参数）
            var files = outcome.Items.Take(DefaultLimit).ToList();
            var truncated = outcome.Items.Count > DefaultLimit;

            // 构建输出
            var outputLines = new List<string>();

            if (files.Count == 0)
            {
                outputLines.Add("未找到匹配文件");
            }
            else
            {
                outputLines.AddRange(files);

                if (truncated)
                {
                    outputLines.Add("");
                    outputLines.Add($"(结果已截断: 显示前 {DefaultLimit} 个结果。考虑使用更具体的路径或模式。)");
                }
            }

            if (outcome.Fallback is not null)
            {
                outputLines.Add("");
                outputLines.Add($"(ripgrep 不可用，已回退托管搜索: {outcome.Fallback.Reason})");
            }

            var metadata = new Dictionary<string, object>
            {
                ["count"] = files.Count,
                ["pattern"] = pattern,
                ["path"] = searchPath,
                ["truncated"] = truncated,
                ["backend"] = outcome.Backend
            };

            if (outcome.Fallback is not null)
                metadata["diagnostic"] = outcome.Fallback.Reason;

            return Success(
                Path.GetFileName(searchPath) ?? searchPath,
                string.Join("\n", outputLines),
                metadata
            );
        }
    }
}
