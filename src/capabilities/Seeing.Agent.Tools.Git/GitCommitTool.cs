using Seeing.Agent.Abstractions.Tools;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Seeing.Agent.Tools.Git;

/// <summary>
/// Git Commit 工具 - 提交更改
/// </summary>
public class GitCommitTool : ITool
{
    public string Id => "git_commit";
    public string Description =>
        "暂存并提交 Git 变更；提交前先用 `git_status` 与 `git_diff` 确认。\n\n" +
        "- files 指定要暂存的文件；all=true 暂存全部变更。\n" +
        "- 提交失败时返回 git stderr，不会回滚已暂存内容。";

    /// <summary>工具标签（用于分类和过滤）</summary>
    public IReadOnlyList<string> Tags => Array.Empty<string>();

    /// <summary>工具分类</summary>
    public ToolCategory Category => ToolCategory.ExternalService;

    public JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            message = new
            {
                type = "string",
                description = "提交信息（必填）"
            },
            files = new
            {
                type = "array",
                items = new { type = "string", description = "文件路径" },
                description = "要暂存并提交的文件列表（可选，缺省配合 all 的行为）"
            },
            all = new
            {
                type = "boolean",
                description = "提交前暂存全部变更（默认 false）"
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
        var match = Regex.Match(output, @"\[.*? ([a-f0-9]+)\]");
        return match.Success ? match.Groups[1].Value : "unknown";
    }
}
