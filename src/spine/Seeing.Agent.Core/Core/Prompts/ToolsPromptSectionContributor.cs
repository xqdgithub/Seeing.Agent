using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Prompts;
using System.Text;

namespace Seeing.Agent.Core.Prompts;

/// <summary>
/// Core 内置工具分节贡献者 — 根据 <see cref="PromptContext.Tools"/> 生成工具列表。
/// <para>
/// 为保持厂商侧提示缓存稳定并压缩 system 体积，此处仅渲染工具名称与一行描述，
/// 完整参数 schema 交由 API 请求的 <c>tools</c> 字段承载（OpenAI / Anthropic 均支持），
/// 不在 system prompt 中重复展开。
/// </para>
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
        }

        return sb.ToString().TrimEnd();
    }
}
