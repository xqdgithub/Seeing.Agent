using Seeing.Agent.Abstractions.Tools;
using System.Text;
using System.Text.Json;

namespace Seeing.Agent.Core.Tools.Git;

/// <summary>
/// Git Log 工具 - 获取提交历史
/// </summary>
public class GitLogTool : ITool
{
    /// <summary>默认提交条数。</summary>
    private const int DefaultMaxCount = 20;

    /// <summary>提交条数上限（防止一次拉取过多历史）。</summary>
    private const int MaxMaxCount = 500;

    public string Id => "git_log";
    public string Description => "Get the commit history of the repository";

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
                description = "Optional path to get history for"
            },
            maxCount = new
            {
                type = "integer",
                minimum = 1,
                maximum = MaxMaxCount,
                description = $"Maximum number of commits to return (max {MaxMaxCount})"
            },
            since = new
            {
                type = "string",
                description = "Show commits since date (e.g., '2 weeks ago')"
            }
        }
    });

    private readonly IGitService _gitService;

    public GitLogTool(IGitService gitService)
    {
        _gitService = gitService;
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        var path = arguments.TryGetProperty("path", out var pathProp)
            ? pathProp.GetString()
            : null;
        var maxCount = arguments.TryGetProperty("maxCount", out var countProp)
            ? Math.Clamp(countProp.GetInt32(), 1, MaxMaxCount)
            : DefaultMaxCount;
        var since = arguments.TryGetProperty("since", out var sinceProp)
            ? sinceProp.GetString()
            : null;

        try
        {
            var commits = await _gitService.GetLogAsync(path, maxCount, since, null, context.CancellationToken);

            var output = new StringBuilder();
            output.AppendLine($"Commits: {commits.Count}");
            output.AppendLine();

            foreach (var commit in commits)
            {
                output.AppendLine($"{commit.ShortHash} {commit.Message}");
                output.AppendLine($"  Author: {commit.Author} <{commit.AuthorEmail}>");
                output.AppendLine($"  Date:   {commit.Date:yyyy-MM-dd HH:mm:ss}");
                output.AppendLine();
            }

            return new ToolResult
            {
                Success = true,
                Title = $"Git Log ({commits.Count} commits)",
                Output = output.ToString(),
                Metadata = new Dictionary<string, object>
                {
                    ["commitCount"] = commits.Count
                }
            };
        }
        catch (GitException ex)
        {
            return new ToolResult
            {
                Success = false,
                Title = "Git Log Failed",
                Output = ex.Message,
                Error = ex.Message
            };
        }
    }
}
