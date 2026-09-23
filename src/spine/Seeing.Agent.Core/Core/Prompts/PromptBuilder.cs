using Seeing.Agent.Abstractions.Prompts;
using System.Text;

namespace Seeing.Agent.Core.Prompts;

/// <summary>
/// 提示词构建器 — 统一构建系统提示词。
/// <para>
/// 通过 <see cref="IPromptSectionContributor"/> 向固定锚点标题注入分节内容：
/// <c>## Tools</c> / <c>## Skills</c> / <c>## Agents</c> / <c>## Environment</c>。
/// 模板含对应锚点时精确注入其下方；Skills / Agents / Environment 缺少锚点时按稳定顺序兜底追加到末尾。
/// Tools 例外：无 <c>## Tools</c> 锚点时不追加（完整 schema 已由 API <c>tools</c> 参数承载，避免冗余）。
/// 另支持自定义变量 <c>{{variable_name}}</c> 与内置变量（model、session_id 等）。
/// </para>
/// </summary>
public class PromptBuilder
{
    /// <summary>分节名 → 锚点标题（须与内置 Agent prompt 中的标题一致）。</summary>
    internal static readonly IReadOnlyDictionary<string, string> SectionAnchors =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [PromptSectionNames.Tools] = "## Tools",
            [PromptSectionNames.Skills] = "## Skills",
            [PromptSectionNames.Agents] = "## Agents",
            [PromptSectionNames.Environment] = "## Environment",
        };

    private readonly IReadOnlyList<IPromptSectionContributor> _contributors;

    /// <summary>
    /// 构造提示词构建器，收集全部提示词分节贡献者。
    /// </summary>
    public PromptBuilder(IEnumerable<IPromptSectionContributor> contributors)
    {
        _contributors = contributors?.ToList() ?? [];
    }

    /// <summary>
    /// 构建完整的系统提示词
    /// </summary>
    public async Task<string> BuildAsync(PromptContext context, CancellationToken cancellationToken = default)
    {
        var basePrompt = context.Agent?.SystemPrompt;

        if (string.IsNullOrEmpty(basePrompt))
        {
            return string.Empty;
        }

        var result = await InjectSectionsAsync(basePrompt, context, cancellationToken).ConfigureAwait(false);

        foreach (var (key, value) in context.Variables)
        {
            result = result.Replace($"{{{{{key}}}}}", value);
        }

        result = ReplaceBuiltinVariables(result, context);

        return result.Trim();
    }

    /// <summary>
    /// 同步构建（向后兼容）。内部调用 <see cref="BuildAsync"/>。
    /// </summary>
    public string Build(string basePrompt, PromptContext context)
    {
        if (string.IsNullOrEmpty(basePrompt))
            return string.Empty;

        var previousAgent = context.Agent;
        var previousPrompt = previousAgent?.SystemPrompt;
        if (previousAgent == null)
            context.Agent = new Seeing.Agent.Abstractions.Agents.AgentDefinition { SystemPrompt = basePrompt };
        else
            previousAgent.SystemPrompt = basePrompt;

        try
        {
            return BuildAsync(context).GetAwaiter().GetResult();
        }
        finally
        {
            if (previousAgent == null)
                context.Agent = null;
            else
                previousAgent.SystemPrompt = previousPrompt;
        }
    }

    private async Task<string> InjectSectionsAsync(
        string prompt,
        PromptContext context,
        CancellationToken cancellationToken)
    {
        var bySection = _contributors
            .GroupBy(c => c.SectionName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(c => c.Order).ToList(),
                StringComparer.OrdinalIgnoreCase);

        var injections = new Dictionary<string, string>(StringComparer.Ordinal);
        // Skills / Agents / Environment：缺锚点时兜底追加到末尾（Tools 见下方例外）
        var appendedSections = new List<string>();

        foreach (var (sectionName, contributors) in bySection)
        {
            if (!SectionAnchors.TryGetValue(sectionName, out var heading))
                continue;

            var parts = new List<string>();
            foreach (var contributor in contributors)
            {
                var part = await contributor.BuildAsync(context, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(part))
                    parts.Add(part.TrimEnd());
            }

            if (parts.Count == 0)
                continue;

            var content = string.Join("\n\n", parts);
            if (ContainsHeading(prompt, heading))
            {
                injections[heading] = content;
            }
            else if (!string.Equals(sectionName, PromptSectionNames.Tools, StringComparison.OrdinalIgnoreCase))
            {
                // Tools 无锚点不兜底追加：工具已通过 API tools 字段传入，system 内重复冗余
                appendedSections.Add($"{heading}\n\n{content}");
            }
        }

        // 按锚点定义顺序排序，保证追加分节顺序稳定（Skills → Agents → Environment；Tools 不兜底）
        var anchorOrder = SectionAnchors.Values.ToList();
        appendedSections = appendedSections
            .OrderBy(s => anchorOrder.FindIndex(h => s.StartsWith(h, StringComparison.Ordinal)))
            .ToList();

        var result = ApplyInjections(prompt, injections);

        if (appendedSections.Count > 0)
        {
            var sb = new StringBuilder(result);
            if (!result.EndsWith('\n'))
                sb.Append('\n');
            sb.Append('\n');
            sb.Append(string.Join("\n\n", appendedSections));
            result = sb.ToString();
        }

        return result;
    }

    internal static bool ContainsHeading(string prompt, string heading)
    {
        foreach (var line in prompt.Replace("\r\n", "\n").Split('\n'))
        {
            if (string.Equals(line.Trim(), heading, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 将分节内容注入到对应 H2 锚点标题下方（直到下一个 H2 或文末）。
    /// </summary>
    internal static string ApplyInjections(string prompt, IReadOnlyDictionary<string, string> injections)
    {
        if (injections.Count == 0)
            return prompt;

        var normalized = prompt.Replace("\r\n", "\n");
        var lines = normalized.Split('\n');
        var sb = new StringBuilder();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            sb.Append(line);
            if (i < lines.Length - 1 || normalized.EndsWith('\n'))
                sb.Append('\n');

            var trimmed = line.Trim();
            if (!injections.TryGetValue(trimmed, out var content))
                continue;

            // 跳过锚点下现有内容，直至下一个 H2
            var j = i + 1;
            while (j < lines.Length)
            {
                var next = lines[j].TrimStart();
                if (next.StartsWith("## ", StringComparison.Ordinal) &&
                    !next.StartsWith("###", StringComparison.Ordinal))
                {
                    break;
                }

                j++;
            }

            sb.Append('\n');
            sb.Append(content);
            sb.Append('\n');
            if (j < lines.Length)
                sb.Append('\n');

            i = j - 1;
        }

        return sb.ToString().TrimEnd('\n') + (normalized.EndsWith('\n') ? "\n" : string.Empty);
    }

    private static string ReplaceBuiltinVariables(string prompt, PromptContext context)
    {
        var result = prompt;

        if (!string.IsNullOrEmpty(context.ModelName))
            result = result.Replace("{{model}}", context.ModelName);

        if (!string.IsNullOrEmpty(context.SessionId))
            result = result.Replace("{{session_id}}", context.SessionId);

        if (!string.IsNullOrEmpty(context.WorkingDirectory))
            result = result.Replace("{{working_directory}}", context.WorkingDirectory);

        result = result.Replace("{{timestamp}}", context.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"));

        return result;
    }
}
