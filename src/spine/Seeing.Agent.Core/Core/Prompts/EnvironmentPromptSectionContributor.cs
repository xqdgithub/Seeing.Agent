using Seeing.Agent.Abstractions.Prompts;
using System.Text;

namespace Seeing.Agent.Core.Prompts;

/// <summary>
/// Core 内置环境分节贡献者 — 工作目录、平台、日期等运行时信息。
/// </summary>
public sealed class EnvironmentPromptSectionContributor : IPromptSectionContributor
{
    /// <inheritdoc />
    public string SectionName => PromptSectionNames.Environment;

    /// <inheritdoc />
    public int Order => 400;

    /// <inheritdoc />
    public Task<string?> BuildAsync(PromptContext context, CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<env>");
        sb.AppendLine($"Working directory: {context.WorkingDirectory ?? "unknown"}");
        if (!string.IsNullOrEmpty(context.WorkspaceRoot))
            sb.AppendLine($"Workspace root: {context.WorkspaceRoot}");
        if (!string.IsNullOrEmpty(context.Platform))
            sb.AppendLine($"Platform: {context.Platform}");
        sb.AppendLine($"Today's date: {context.Timestamp:yyyy-MM-dd}");
        sb.AppendLine("</env>");

        return Task.FromResult<string?>(sb.ToString().TrimEnd());
    }
}
