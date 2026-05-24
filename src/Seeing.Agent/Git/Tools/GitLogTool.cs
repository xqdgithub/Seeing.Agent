using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using System.Text;
using System.Text.Json;

namespace Seeing.Agent.Git.Tools
{
    /// <summary>
    /// Git Log 工具 - 获取提交历史
    /// </summary>
    public class GitLogTool : ITool
    {
        public string Id => "git_log";
        public string Description => "Get the commit history of the repository";

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
                    description = "Maximum number of commits to return"
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
                ? countProp.GetInt32()
                : 20;
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
}
