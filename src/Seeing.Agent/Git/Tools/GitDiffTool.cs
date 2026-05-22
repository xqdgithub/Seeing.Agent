using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Git;

namespace Seeing.Agent.Git.Tools
{
    /// <summary>
    /// Git Diff 工具 - 获取差异
    /// </summary>
    public class GitDiffTool : ITool
    {
        public string Id => "git_diff";
        public string Description => "Get the diff of changes in the repository";

        public JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                path = new
                {
                    type = "string",
                    description = "Optional path to get diff for"
                },
                staged = new
                {
                    type = "boolean",
                    description = "Show staged changes only"
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
}
