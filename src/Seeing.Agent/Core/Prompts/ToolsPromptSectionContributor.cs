using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Prompts;
using System.Text;
using System.Text.Json;

namespace Seeing.Agent.Core.Prompts;

/// <summary>
/// Core 内置工具分节贡献者 — 根据 <see cref="PromptContext.Tools"/> 生成工具列表。
/// </summary>
public sealed class ToolsPromptSectionContributor : IPromptSectionContributor
{
    /// <inheritdoc />
    public string SectionName => PromptSectionNames.Tools;

    /// <inheritdoc />
    public int Order => 100;

    /// <inheritdoc />
    public Task<string?> BuildAsync(PromptContext context, CancellationToken cancellationToken = default)
    {
        if (context.Tools == null)
            return Task.FromResult<string?>(null);

        return Task.FromResult<string?>(BuildToolSection(context.Tools));
    }

    internal static string BuildToolSection(IEnumerable<FunctionSchema> tools)
    {
        var toolList = tools.ToList();
        if (toolList.Count == 0)
            return "暂无可用工具。";

        var sb = new StringBuilder();
        sb.AppendLine("以下工具可供调用：");
        sb.AppendLine();

        foreach (var tool in toolList)
        {
            sb.AppendLine($"### {tool.Name}");
            if (!string.IsNullOrEmpty(tool.Description))
                sb.AppendLine(tool.Description);
            sb.AppendLine();

            if (tool.Parameters.HasValue)
            {
                var parameters = tool.Parameters.Value;
                if (parameters.ValueKind == JsonValueKind.Object &&
                    parameters.TryGetProperty("properties", out var properties))
                {
                    sb.AppendLine("**参数：**");
                    foreach (var prop in properties.EnumerateObject())
                    {
                        sb.AppendLine($"- `{prop.Name}`: {GetPropertyDescription(prop.Value)}");
                    }
                    sb.AppendLine();
                }
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string GetPropertyDescription(JsonElement property)
    {
        if (property.ValueKind != JsonValueKind.Object)
            return "未知类型";

        var sb = new StringBuilder();

        if (property.TryGetProperty("type", out var typeElement))
            sb.Append(typeElement.GetString() ?? "unknown");

        if (property.TryGetProperty("description", out var descElement))
        {
            var desc = descElement.GetString();
            if (!string.IsNullOrEmpty(desc))
                sb.Append($" - {desc}");
        }

        if (property.TryGetProperty("required", out var requiredElement) && requiredElement.GetBoolean())
            sb.Append(" (必需)");

        return sb.ToString();
    }
}
