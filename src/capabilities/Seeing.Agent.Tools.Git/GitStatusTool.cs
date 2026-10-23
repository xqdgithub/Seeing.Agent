using Seeing.Agent.Abstractions.Tools;
using System.Text.Json;

namespace Seeing.Agent.Tools.Git;

/// <summary>
/// Git Status 工具 - 获取仓库状态
/// </summary>
public class GitStatusTool : ITool
{
    public string Id => "git_status";
    public string Description =>
        "查看 Git 仓库状态（分支、领先落后、变更文件）。\n\n" +
        "- 返回分支、是否干净与变更文件列表（含暂存标记）。\n" +
        "- 查看具体差异用 `git_diff`。";

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
                description = "仓库或子目录路径，默认当前工作目录"
            }
        }
    });

    private readonly IGitService _gitService;

    public GitStatusTool(IGitService gitService)
    {
        _gitService = gitService;
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        var path = arguments.TryGetProperty("path", out var pathProp)
            ? pathProp.GetString()
            : null;

        try
        {
            var status = await _gitService.GetStatusAsync(path, context.CancellationToken);

            var output = $"Branch: {status.Branch}\n";
            output += $"Clean: {status.IsClean}\n";

            if (status.Ahead != null || status.Behind != null)
            {
                output += $"Tracking: {status.Ahead ?? ""} {status.Behind ?? ""}\n";
            }

            if (status.Files.Count > 0)
            {
                output += "\nFiles:\n";
                foreach (var file in status.Files)
                {
                    var state = file.State switch
                    {
                        GitFileState.Modified => "M",
                        GitFileState.Added => "A",
                        GitFileState.Deleted => "D",
                        GitFileState.Untracked => "?",
                        GitFileState.Renamed => "R",
                        _ => " "
                    };
                    var staged = file.IsStaged ? "S" : " ";
                    output += $"  [{staged}{state}] {file.Path}\n";
                }
            }

            return new ToolResult
            {
                Success = true,
                Title = $"Git Status: {status.Branch}",
                Output = output,
                Metadata = new Dictionary<string, object>
                {
                    ["branch"] = status.Branch,
                    ["isClean"] = status.IsClean,
                    ["fileCount"] = status.Files.Count
                }
            };
        }
        catch (GitException ex)
        {
            return new ToolResult
            {
                Success = false,
                Title = "Git Status Failed",
                Output = ex.Message,
                Error = ex.Message
            };
        }
    }
}
