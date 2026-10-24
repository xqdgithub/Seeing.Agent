using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Prompts;
using System.Text;

namespace Seeing.Agent.Core.Prompts;

/// <summary>
/// 提示词构建器 — 统一构建系统提示词。
/// <para>
/// 各 <see cref="IPromptSectionContributor"/> 自述 <c>Heading</c>（分节标题）与 <c>Placement</c>
/// （追加末尾 / 插入到某已命名分节之后）。构建器不认识任何具体分节名，只按声明定位：
/// 模板中存在与 <c>Heading</c> 一致的标题行时，内容精确注入该锚点区（跳过锚点下现有内容直至下一个 H2）；
/// 缺少锚点时按 "Heading + 换行 + body" 渲染，再按 <c>Placement</c> 追加（After 锚点未找到 → 末尾追加并记 Debug）。
/// <c>Heading</c> 为空的贡献者：记 Error 日志（结构化 <c>{SectionName}</c>）并跳过，不静默、不抛异常。
/// </para>
/// <para>
/// 另支持自定义变量 <c>{{variable_name}}</c> 与内置变量（model、session_id 等）。
/// </para>
/// </summary>
public class PromptBuilder
{
    private readonly IReadOnlyList<IPromptSectionContributor> _contributors;
    private readonly ILogger<PromptBuilder> _logger;

    /// <summary>
    /// 构造提示词构建器，收集全部提示词分节贡献者。
    /// </summary>
    public PromptBuilder(
        IEnumerable<IPromptSectionContributor> contributors,
        ILogger<PromptBuilder>? logger = null)
    {
        _contributors = contributors?.ToList() ?? [];
        _logger = logger ?? NullLogger<PromptBuilder>.Instance;
    }

    /// <summary>
    /// 构建完整的系统提示词
    /// <para>顺序固定：分节注入 → 自定义变量替换 → 内置变量替换 → Trim → 追加上下文基线。
    /// 基线最后追加（且不再参与变量替换），保证基线内的字面量逐请求不变，前缀缓存不被击穿。</para>
    /// </summary>
    public async Task<string> BuildAsync(PromptContext context, CancellationToken cancellationToken = default)
    {
        var basePrompt = context.Agent?.SystemPrompt;

        var result = string.Empty;

        // 不再因系统提示词为空而整体跳过：基线仍须作为系统提示词送达模型
        if (!string.IsNullOrEmpty(basePrompt))
        {
            result = await InjectSectionsAsync(basePrompt, context, cancellationToken).ConfigureAwait(false);

            foreach (var (key, value) in context.Variables)
            {
                result = result.Replace($"{{{{{key}}}}}", value);
            }

            result = ReplaceBuiltinVariables(result, context);

            result = result.Trim();
        }

        return AppendBaseline(result, context.ContextBaseline);
    }

    /// <summary>
    /// 在提示词末尾追加基线文本：正文为空时基线即系统提示词；否则以空行分隔追加。
    /// </summary>
    private static string AppendBaseline(string prompt, string? baseline)
    {
        if (string.IsNullOrWhiteSpace(baseline))
            return prompt;

        return string.IsNullOrEmpty(prompt) ? baseline : prompt + "\n\n" + baseline;
    }

    /// <summary>按 SectionName 聚合后的分节（组级 Heading/Placement 取首个登记贡献者的声明）。</summary>
    private sealed record SectionGroup(
        string SectionName,
        string Heading,
        PromptSectionPlacement Placement,
        int Order,
        List<IPromptSectionContributor> Members);

    private async Task<string> InjectSectionsAsync(
        string prompt,
        PromptContext context,
        CancellationToken cancellationToken)
    {
        var groups = new List<SectionGroup>();
        foreach (var contributor in _contributors)
        {
            if (string.IsNullOrWhiteSpace(contributor.Heading))
            {
                _logger.LogError(
                    "提示词分节贡献者未声明 Heading，跳过该分节: {SectionName}",
                    contributor.SectionName);
                continue;
            }

            var index = groups.FindIndex(
                g => string.Equals(g.SectionName, contributor.SectionName, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                var group = groups[index];
                group.Members.Add(contributor);
                group.Members.Sort(static (a, b) => a.Order.CompareTo(b.Order));
            }
            else
            {
                groups.Add(new SectionGroup(
                    contributor.SectionName,
                    contributor.Heading,
                    contributor.Placement,
                    contributor.Order,
                    [contributor]));
            }
        }

        var injections = new Dictionary<string, string>(StringComparer.Ordinal);
        // 无锚点分节按 Placement 声明追加到末尾区域
        var appended = new List<(string SectionName, string Block)>();

        foreach (var group in groups.OrderBy(g => g.Order).ToList())
        {
            var parts = new List<string>();
            foreach (var contributor in group.Members)
            {
                var part = await contributor.BuildAsync(context, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(part))
                    parts.Add(part.TrimEnd());
            }

            if (parts.Count == 0)
                continue;

            var content = string.Join("\n\n", parts);
            if (ContainsHeading(prompt, group.Heading))
            {
                injections[group.Heading] = content;
                continue;
            }

            var block = $"{group.Heading}\n\n{content}";
            if (group.Placement.AppendAtEnd || string.IsNullOrWhiteSpace(group.Placement.AfterSection))
            {
                appended.Add((group.SectionName, block));
            }
            else
            {
                var anchorIndex = appended.FindIndex(
                    e => string.Equals(e.SectionName, group.Placement.AfterSection, StringComparison.OrdinalIgnoreCase));
                if (anchorIndex >= 0)
                {
                    appended.Insert(anchorIndex + 1, (group.SectionName, block));
                }
                else
                {
                    _logger.LogDebug(
                        "提示词放置锚点分节未找到: {AfterSection}（当前: {SectionName}），退化为末尾追加",
                        group.Placement.AfterSection, group.SectionName);
                    appended.Add((group.SectionName, block));
                }
            }
        }

        var result = ApplyInjections(prompt, injections);

        if (appended.Count > 0)
        {
            var sb = new StringBuilder(result);
            if (!result.EndsWith('\n'))
                sb.Append('\n');
            sb.Append('\n');
            sb.Append(string.Join("\n\n", appended.Select(a => a.Block)));
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
