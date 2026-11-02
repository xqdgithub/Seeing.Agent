using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Skills;
using Seeing.Agent.Reflection.Configuration;

namespace Seeing.Agent.Reflection.Core;

/// <summary>
/// 把解析出的建议按当前模式落到技能库：复盘唯一的写入出口。
/// <para>
/// <b>为什么写入是执行器的职责而不是 agent 的工具</b>：
/// </para>
/// <list type="number">
/// <item>复盘 agent 的产出已经是结构化 JSON（见 <see cref="Prompts"/>），执行器本来就逐条解析。
/// 再给 agent 一个写工具，等于让它用两种互斥的方式表达同一件事——
/// 提示词要求输出 JSON，工具清单却提供写入通道，模型只能猜。</item>
/// <item>工具写盘的结果对执行器完全不可见：journal 里的 <c>CreatedSkills</c> 只能是硬编码空数组，
/// 「这轮复盘写了什么」永远查不到。收进执行器后写入结果与审计记录天然同源。</item>
/// <item>复盘 agent 因此可以是<b>纯只读</b>的：不需要写文件权限、不需要 shell，
/// 权限面从「必须允许写文件」收窄到「完全不允许写」，安全边界更容易论证。</item>
/// </list>
/// <para>
/// 校验顺序对齐平台既有口径（<c>SkillWriter.TryValidateName</c> 与 <c>SkillFileScanner</c>），
/// 不自创新规则：名称正则、路径字符、正文长度、注入片段。
/// </para>
/// </summary>
public sealed class ReflectionSuggestionsWriter
{
    /// <summary>技能名最大长度（与 SKILL.md frontmatter 约定一致）。</summary>
    public const int MaxNameLength = 64;

    /// <summary>技能描述最大长度。</summary>
    public const int MaxDescriptionLength = 1024;

    /// <summary>技能正文最大长度。</summary>
    public const int MaxContentLength = 8000;

    /// <summary>技能名中禁止出现的字符（跨平台文件名非法字符 + 路径分隔符）。</summary>
    private static readonly char[] s_illegalNameChars = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>
    /// 正文中禁止出现的提示注入片段（大小写不敏感）。
    /// <para>技能正文会被注入后续会话的上下文，形如「忽略之前的指令」的句子等于把
    /// 一条可执行指令藏进数据通道，必须在写入前拒绝。</para>
    /// </summary>
    private static readonly string[] s_injectionPatterns =
    [
        "忽略之前",
        "忽略上面",
        "ignore previous",
        "ignore above",
        "disregard previous"
    ];

    private readonly ISkillWriter? _writer;
    private readonly ISkillCatalog? _catalog;
    private readonly IOptionsMonitor<ReflectionOptions> _options;
    private readonly ILogger<ReflectionSuggestionsWriter> _logger;

    /// <summary>
    /// 创建建议写入器。
    /// <para>写入通道为可选依赖：未装配技能能力包时仍可构造，
    /// 只是 ApplyCreate 模式会逐条记「通道不可用」而不是让复盘整体失败。</para>
    /// </summary>
    public ReflectionSuggestionsWriter(
        IOptionsMonitor<ReflectionOptions> options,
        ILogger<ReflectionSuggestionsWriter> logger,
        ISkillWriter? writer = null,
        ISkillCatalog? catalog = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _writer = writer;
        _catalog = catalog;
    }

    /// <summary>
    /// 按当前模式处理建议清单。
    /// <para>
    /// <see cref="ReflectionMode.SuggestOnly"/> / <see cref="ReflectionMode.Off"/> 一律不写盘——
    /// 建议内容已经在 journal 的 <c>Suggestions</c> 里完整保留，无需额外动作。
    /// </para>
    /// </summary>
    /// <param name="suggestions">解析出的建议。</param>
    /// <param name="sourceSessionId">源会话 ID，写入 frontmatter 供追溯。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>实际创建成功的技能名。</returns>
    public async Task<IReadOnlyList<string>> ApplyAsync(
        IReadOnlyList<ReflectionSuggestion> suggestions,
        string sourceSessionId,
        CancellationToken ct = default)
    {
        if (suggestions is null || suggestions.Count == 0)
            return Array.Empty<string>();

        var mode = _options.CurrentValue.Mode;
        if (mode != ReflectionMode.ApplyCreate)
            return Array.Empty<string>();

        if (_writer is null)
        {
            // 不该静默：用户显式开了 ApplyCreate，却因为没有技能能力包而什么都没写进去。
            _logger.LogWarning(
                "复盘处于 ApplyCreate 模式但技能写入通道不可用（未装配技能能力包），本次未写入任何技能：SourceSessionId={SourceSessionId}",
                sourceSessionId);
            return Array.Empty<string>();
        }

        var created = new List<string>(suggestions.Count);

        foreach (var suggestion in suggestions)
        {
            ct.ThrowIfCancellationRequested();

            var outcome = await TryCreateAsync(suggestion, sourceSessionId, ct).ConfigureAwait(false);
            if (outcome is not null)
                created.Add(outcome);
        }

        return created;
    }

    /// <summary>
    /// 尝试创建一条技能；返回创建成功的名称，跳过时返回 <c>null</c>。
    /// <para>单条失败不影响其余条目：复盘是批量建议，一条命名冲突不该让整批白跑。</para>
    /// </summary>
    private async Task<string?> TryCreateAsync(
        ReflectionSuggestion suggestion,
        string sourceSessionId,
        CancellationToken ct)
    {
        var name = suggestion.Target?.Trim();
        var content = suggestion.SuggestedContent?.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            _logger.LogDebug("跳过建议：target 为空");
            return null;
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            // 只有 target 没有正文 = 模型只提了「要有个技能叫 X」，没有可落盘的内容。
            _logger.LogDebug("跳过建议：suggestedContent 为空（Skill={Skill}）", name);
            return null;
        }

        if (name.Length > MaxNameLength)
        {
            _logger.LogDebug("跳过建议：名称超长（{Length}>{Max}，Skill={Skill}）", name.Length, MaxNameLength, name);
            return null;
        }

        if (name.IndexOfAny(s_illegalNameChars) >= 0 || name.Any(char.IsControl))
        {
            _logger.LogDebug("跳过建议：名称含非法字符（Skill={Skill}）", name);
            return null;
        }

        if (content.Length > MaxContentLength)
        {
            _logger.LogDebug("跳过建议：正文超长（{Length}>{Max}，Skill={Skill}）", content.Length, MaxContentLength, name);
            return null;
        }

        var injection = s_injectionPatterns.FirstOrDefault(
            p => content.Contains(p, StringComparison.OrdinalIgnoreCase));
        if (injection is not null)
        {
            // 这是安全判定而非格式问题，必须留下可见痕迹。
            _logger.LogWarning(
                "跳过建议：正文含疑似提示注入片段「{Pattern}」（Skill={Skill}）",
                injection,
                name);
            return null;
        }

        // 查重：同名技能一律不覆盖，避免把用户既有技能静默改掉。
        if (_catalog?.Find(name) is not null)
        {
            _logger.LogDebug("跳过建议：已存在同名技能，不覆盖（Skill={Skill}）", name);
            return null;
        }

        // 描述缺失时用名称兜底：SkillDraft 要求非空，但模型漏写 description 不该让整条建议作废。
        var description = string.IsNullOrWhiteSpace(suggestion.Reason)
            ? name
            : Truncate(suggestion.Reason.Trim(), MaxDescriptionLength);

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["distilled-by"] = "seeing-reflection",
            ["source-session"] = sourceSessionId ?? string.Empty
        };

        try
        {
            var result = await _writer.CreateAsync(
                new SkillDraft(name, description, content, metadata),
                ConfigLevel.Project,
                overwrite: false,
                ct).ConfigureAwait(false);

            if (!result.Success)
            {
                _logger.LogWarning(
                    "复盘技能写入失败：Skill={Skill}, Reason={Reason}",
                    name,
                    result.Error ?? "未知原因");
                return null;
            }

            _logger.LogInformation("复盘已写入技能：Skill={Skill}, SourceSessionId={SourceSessionId}", name, sourceSessionId);
            return name;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "复盘技能写入异常：Skill={Skill}", name);
            return null;
        }
    }

    /// <summary>按字符数截断，超长时追加省略标记。</summary>
    private static string Truncate(string text, int limit)
        => text.Length <= limit ? text : text[..limit] + "…";
}
