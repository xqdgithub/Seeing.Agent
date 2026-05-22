using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Git;

namespace Seeing.Agent.Git.Tools
{
    /// <summary>
    /// Git Commit 工具 - 提交更改
    /// </summary>
    public class GitCommitTool : ITool
    {
        public string Id => "git_commit";
        public string Description => "Commit changes to the repository";

        public JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                message = new
                {
                    type = "string",
                    description = "Commit message"
                },
                files = new
                {
                    type = "array",
                    items = new { type = "string" },
                    description = "Files to stage and commit (optional, defaults to all)"
                },
                all = new
                {
                    type = "boolean",
                    description = "Stage all changes before commit"
                }
            },
            required = new[] { "message" }
        });

        private readonly IGitService _gitService;

        public GitCommitTool(IGitService gitService)
        {
            _gitService = gitService;
        }

        public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
        {
            var message = arguments.TryGetProperty("message", out var msgProp)
                ? msgProp.GetString()
                : throw new JsonException("Missing required parameter: message");

            var all = arguments.TryGetProperty("all", out var allProp) && allProp.GetBoolean();
            var files = new List<string>();
            if (arguments.TryGetProperty("files", out var filesProp) && filesProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var file in filesProp.EnumerateArray())
                {
                    if (file.GetString() is string f)
                        files.Add(f);
                }
            }

            try
            {
                // Stage files
                if (all || files.Count > 0)
                {
                    var addResult = await _gitService.AddAsync(files.Count > 0 ? files : null, all, context.CancellationToken);
                    if (!addResult.Success)
                    {
                        return new ToolResult
                        {
                            Success = false,
                            Title = "Git Add Failed",
                            Output = addResult.StdErr,
                            Error = addResult.StdErr
                        };
                    }
                }

                // Commit
                var commitResult = await _gitService.CommitAsync(message!, cancellationToken: context.CancellationToken);
                if (!commitResult.Success)
                {
                    return new ToolResult
                    {
                        Success = false,
                        Title = "Git Commit Failed",
                        Output = commitResult.StdErr,
                        Error = commitResult.StdErr
                    };
                }

                // Get the commit hash
                var hash = ExtractCommitHash(commitResult.StdOut);

                return new ToolResult
                {
                    Success = true,
                    Title = $"Committed: {hash}",
                    Output = $"[{hash}] {message}",
                    Metadata = new Dictionary<string, object>
                    {
                        ["hash"] = hash,
                        ["message"] = message!
                    }
                };
            }
            catch (GitException ex)
            {
                return new ToolResult
                {
                    Success = false,
                    Title = "Git Commit Failed",
                    Output = ex.Message,
                    Error = ex.Message
                };
            }
        }

        private static string ExtractCommitHash(string output)
        {
            // Extract from "[branch abc123]" format
            var match = System.Text.RegularExpressions.Regex.Match(output, @"\[.*? ([a-f0-9]+)\]");
            return match.Success ? match.Groups[1].Value : "unknown";
        }
    }
}
