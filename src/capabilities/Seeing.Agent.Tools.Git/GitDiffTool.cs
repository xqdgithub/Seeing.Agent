using Seeing.Agent.Abstractions.Tools;
using System.Text.Json;

namespace Seeing.Agent.Tools.Git;

/// <summary>
/// Git Diff 工具 - 获取差异
/// </summary>
public class GitDiffTool : ITool
{
    public string Id => "git_diff";
    public string Description =>
        "查看 Git 工作区或暂存区的差异。\n\n" +
        "- 返回统一 diff 文本；先看变更清单用 `git_status`。";

    /// <summary>工具标签（用于分类和过滤）</summary>
    public IReadOnlyList<string> Tags => Array.Empty<string>();

    /// <summary>工具分类</summary>
    public ToolCategory Category => ToolCategory.ExternalService;

    public JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            path = new
            {
                type = "string",
                description = "限定差异的文件或目录路径，默认整个仓库"
            },
            staged = new
            {
                type = "boolean",
                description = "只显示已暂存的变更（默认 false，显示工作区变更）"
            }
        }
    });

    private readonly IGitService _gitService;

    public GitDiffTool(IGitService gitService)
    {
        _gitService = gitService;
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        var path = arguments.TryGetProperty("path", out var pathProp)
            ? pathProp.GetString()
            : null;
        var staged = arguments.TryGetProperty("staged", out var stagedProp)
            && stagedProp.GetBoolean();

        try
        {
            var diff = await _gitService.GetDiffAsync(path, staged, context.CancellationToken);

            var output = $"Files: {diff.Files.Count}\n";
            output += $"+{diff.TotalAddedLines} -{diff.TotalDeletedLines}\n\n";
            output += diff.RawDiff;

            return new ToolResult
            {
                Success = true,
                Title = $"Git Diff ({(staged ? "staged" : "all")})",
                Output = output,
                Metadata = new Dictionary<string, object>
                {
                    ["fileCount"] = diff.Files.Count,
                    ["addedLines"] = diff.TotalAddedLines,
                    ["deletedLines"] = diff.TotalDeletedLines
                }
            };
        }
        catch (GitException ex)
        {
            return new ToolResult
            {
                Success = false,
                Title = "Git Diff Failed",
                Output = ex.Message,
                Error = ex.Message
            };
        }
    }
}
