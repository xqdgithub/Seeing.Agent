using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Git;

namespace Seeing.Agent.Git.Tools
{
    /// <summary>
    /// Git Status 工具 - 获取仓库状态
    /// </summary>
    public class GitStatusTool : ITool
    {
        public string Id => "git_status";
        public string Description => "Get the status of the git repository";

        public JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                path = new
                {
                    type = "string",
                    description = "Optional path to check status for"
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
}
