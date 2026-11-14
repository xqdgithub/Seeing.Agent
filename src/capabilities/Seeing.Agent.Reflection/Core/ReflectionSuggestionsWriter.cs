using System.Text.RegularExpressions;
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
/// 名称规则与技能创建页一致（kebab-case），比平台 <c>SkillWriter</c> 更严；
/// 其余校验（正文长度、注入片段、查重）沿用本写入器既有口径。
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

    /// <summary>
    /// 技能名规范：与技能创建页一致——仅小写字母、数字与连字符（kebab-case）。
    /// <para>复盘写入器比平台 <c>SkillWriter</c> 更严：平台为兼容生态保留宽松名，
    /// 复盘产出面向本仓新建技能，必须与 UI 创建口径一致，避免中文名落盘。</para>
    /// </summary>
    private static readonly Regex s_skillNamePattern = new(
        @"^[a-z0-9]+(-[a-z0-9]+)*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

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
    /// 手动把一条建议落成技能（记录页的「生成 SKILL」）。
    /// <para>
    /// <b>不看 <see cref="ReflectionMode"/></b>：模式约束的是「复盘跑完后自动写盘」这个自主行为，
    /// 而这里是用户对着具体建议显式点击——与「手动触发跳过 <c>Enabled</c>」是同一个理由。
    /// 其余校验（名称合法性、正文长度、提示注入、查重）全部复用
    /// <see cref="TryCreateAsync"/>，不另开一套口径。
    /// </para>
    /// </summary>
    /// <param name="suggestion">要落盘的建议（草稿缺省时从中取字段）。</param>
    /// <param name="sourceSessionId">源会话 ID（写入 frontmatter 供追溯）。</param>
    /// <param name="level">目标层级：项目级 / 用户级。</param>
    /// <param name="draft">用户确认过的草稿；为 <c>null</c> 时用建议原文（ApplyCreate / 兼容旧调用）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>写入结果；失败时带可展示原因。</returns>
    public async Task<ReflectionSkillCreationResult> CreateFromSuggestionAsync(
        ReflectionSuggestion suggestion,
        string sourceSessionId,
        ConfigLevel level,
        ReflectionSkillDraft? draft = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(suggestion);

        if (_writer is null)
        {
            return ReflectionSkillCreationResult.Failed(
                "技能写入通道不可用：当前启动能力集不含 skills 模块。");
        }

        var (name, error) = await TryCreateAsync(suggestion, sourceSessionId, level, draft, ct)
            .ConfigureAwait(false);
        return name is null
            ? ReflectionSkillCreationResult.Failed(error ?? "建议无法落成技能。")
            : ReflectionSkillCreationResult.Created(name);
    }

    /// <summary>
    /// 技能名是否符合复盘落盘规范（与技能创建页同一正则）。
    /// </summary>
    /// <param name="name">待校验名称。</param>
    /// <param name="error">失败原因（可直接展示）；成功时为 <c>null</c>。</param>
    /// <returns>合法返回 <c>true</c>。</returns>
    public static bool TryValidateSkillName(string? name, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(name))
        {
            error = "技能名不能为空。";
            return false;
        }

        var trimmed = name.Trim();
        if (trimmed.Length > MaxNameLength)
        {
            error = $"技能名超长（{trimmed.Length} > {MaxNameLength}）。";
            return false;
        }

        if (!s_skillNamePattern.IsMatch(trimmed))
        {
            error = "技能名只能包含小写字母、数字和连字符（如 my-skill）。";
            return false;
        }

        return true;
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

            // 自动路径固定写项目级：这些建议来自「本项目的会话」，落到本项目技能目录才可追溯。
            var (name, _) = await TryCreateAsync(
                suggestion, sourceSessionId, ConfigLevel.Project, draft: null, ct).ConfigureAwait(false);
            if (name is not null)
                created.Add(name);
        }

        return created;
    }

    /// <summary>
    /// 尝试创建一条技能；返回创建成功的名称与失败原因（成功时原因为 <c>null</c>）。
    /// <para>单条失败不影响其余条目：复盘是批量建议，一条命名冲突不该让整批白跑。</para>
    /// </summary>
    /// <param name="suggestion">建议内容。</param>
    /// <param name="sourceSessionId">源会话 ID（frontmatter 追溯）。</param>
    /// <param name="level">目标层级。</param>
    /// <param name="draft">用户草稿；为 <c>null</c> 时用建议字段。</param>
    /// <param name="ct">取消令牌。</param>
    private async Task<(string? Name, string? Error)> TryCreateAsync(
        ReflectionSuggestion suggestion,
        string sourceSessionId,
        ConfigLevel level,
        ReflectionSkillDraft? draft,
        CancellationToken ct)
    {
        // 写入通道在这里再判一次：两个公开入口都已判过，但本方法是唯一真正用 _writer 的地方，
        // 少这一层就只能靠调用方纪律（编译器也会在下一行的调用点上报可空解引用）。
        if (_writer is not { } writer)
            return (null, "技能写入通道不可用：当前启动能力集不含 skills 模块。");

        var name = (draft?.Name ?? suggestion.Target)?.Trim();
        var content = (draft?.Content ?? suggestion.SuggestedContent)?.Trim();
        // 描述只取 description（草稿优先）：reason 是给人看的备注，绝不能当 SKILL 选型描述落盘。
        var descriptionSource = draft?.Description ?? suggestion.Description;

        if (string.IsNullOrWhiteSpace(name))
        {
            _logger.LogDebug("跳过建议：target 为空");
            return (null, "建议缺少技能名（target）。");
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            // 只有 target 没有正文 = 模型只提了「要有个技能叫 X」，没有可落盘的内容。
            _logger.LogDebug("跳过建议：suggestedContent 为空（Skill={Skill}）", name);
            return (null, "该建议只有技能名、没有正文，没有可写入的内容。");
        }

        if (!TryValidateSkillName(name, out var nameError))
        {
            _logger.LogDebug("跳过建议：名称不合规（Skill={Skill}, Reason={Reason}）", name, nameError);
            return (null, nameError);
        }

        if (content.Length > MaxContentLength)
        {
            _logger.LogDebug("跳过建议：正文超长（{Length}>{Max}，Skill={Skill}）", content.Length, MaxContentLength, name);
            return (null, $"技能正文超长（{content.Length} > {MaxContentLength}）。");
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
            return (null, $"技能正文含疑似提示注入片段「{injection}」，已拒绝写入。");
        }

        // 查重：同名技能一律不覆盖，避免把用户既有技能静默改掉。
        if (_catalog?.Find(name) is not null)
        {
            _logger.LogDebug("跳过建议：已存在同名技能，不覆盖（Skill={Skill}）", name);
            return (null, $"已存在同名技能「{name}」，请到技能页编辑或换个名字。");
        }

        // 描述缺失时用名称兜底：SkillDraft 要求非空；不回落 reason（备注叙事会污染选型）。
        var description = string.IsNullOrWhiteSpace(descriptionSource)
            ? name
            : Truncate(descriptionSource.Trim(), MaxDescriptionLength);

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["distilled-by"] = "seeing-reflection",
            ["source-session"] = sourceSessionId ?? string.Empty
        };
        if (!string.IsNullOrWhiteSpace(suggestion.Reason))
            metadata["notes"] = Truncate(suggestion.Reason.Trim(), MaxDescriptionLength);

        try
        {
            var result = await writer.CreateAsync(
                new SkillDraft(name, description, content, metadata),
                level,
                overwrite: false,
                ct).ConfigureAwait(false);

            if (!result.Success)
            {
                _logger.LogWarning(
                    "复盘技能写入失败：Skill={Skill}, Level={Level}, Reason={Reason}",
                    name,
                    level,
                    result.Error ?? "未知原因");
                return (null, result.Error ?? "技能写入失败。");
            }

            _logger.LogInformation(
                "复盘已写入技能：Skill={Skill}, Level={Level}, SourceSessionId={SourceSessionId}",
                name,
                level,
                sourceSessionId);
            return (name, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "复盘技能写入异常：Skill={Skill}", name);
            return (null, ex.Message);
        }
    }

    /// <summary>按字符数截断，超长时追加省略标记。</summary>
    private static string Truncate(string text, int limit)
        => text.Length <= limit ? text : text[..limit] + "…";
}
