using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Skills;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Reflection.Configuration;
using Seeing.Agent.Tools.Support;

namespace Seeing.Agent.Reflection.Integration.Tools;

/// <summary>
/// 把一条可复用的流程沉淀为项目级技能（复盘代理的写入出口）。
/// <para>
/// 工具本身不做权限审批：权限由 <c>ToolManager</c> 映射到执行级授权器统一处理。
/// 写盘与否只由 <see cref="ReflectionOptions.Mode"/> 决定——默认的
/// <see cref="ReflectionMode.SuggestOnly"/> 只产出建议文本，绝不触碰磁盘。
/// </para>
/// </summary>
public sealed class SkillCreateTool : ToolBase
{
    /// <summary>工具 id（模块 ProvidedTools 与注销逻辑共用同一常量）。</summary>
    public const string ToolId = "skill_create";

    /// <summary>技能名最大长度（与 SKILL.md frontmatter 约定一致）。</summary>
    private const int MaxNameLength = 64;

    /// <summary>技能描述最大长度。</summary>
    private const int MaxDescriptionLength = 1024;

    /// <summary>技能正文最大长度。</summary>
    private const int MaxContentLength = 8000;

    /// <summary>技能名中禁止出现的字符（跨平台文件名非法字符 + 路径分隔符）。</summary>
    private static readonly char[] s_illegalNameChars = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>
    /// 正文中禁止出现的提示注入片段（大小写不敏感）。
    /// <para>技能正文会被注入后续会话的上下文，形如「忽略之前的指令」的句子等于把
    /// 一个可执行指令藏进数据通道，必须在写入前拒绝。</para>
    /// </summary>
    private static readonly string[] s_injectionPatterns =
    [
        "忽略之前",
        "忽略上面",
        "ignore previous",
        "ignore above",
        "disregard previous"
    ];

    private readonly IOptionsMonitor<ReflectionOptions> _options;
    private readonly ISkillWriter? _writer;
    private readonly ISkillCatalog? _catalog;

    /// <summary>
    /// 创建技能写入工具。
    /// <para>写作通道为可选依赖：未装配技能能力包时工具仍可解析，
    /// 但要落盘会明确报错，而不是让整个复盘模块起不来。</para>
    /// </summary>
    public SkillCreateTool(
        ILogger<SkillCreateTool> logger,
        IOptionsMonitor<ReflectionOptions> options,
        ISkillWriter? writer = null,
        ISkillCatalog? catalog = null) : base(logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _writer = writer;
        _catalog = catalog;
    }

    /// <inheritdoc />
    public override string Id => ToolId;

    /// <inheritdoc />
    public override string Description =>
        "把一条可复用的流程沉淀为项目级技能。\n\n" +
        "- 何时使用：复盘时发现「下次遇到同类任务可以照做」的流程性经验（顺序、分支、检查点）。\n" +
        "- 何时不要使用：一次性的调试叙事（本次堆栈、临时文件名）、环境偶发失败（网络抖动、临时缺包）、" +
        "对工具的负面断言（「X 工具不能用」）、能从代码直接读出的事实（函数签名、目录结构）。\n" +
        "- 写入行为：ApplyCreate 模式下直接写入项目级技能目录；SuggestOnly（及 Off）模式下只把建议回显给你，不写盘。\n" +
        "- 同名技能不会覆盖：若已存在同名技能，本次调用失败并提示改名。";

    /// <inheritdoc />
    public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            name = new
            {
                type = "string",
                maxLength = MaxNameLength,
                description =
                    "技能名（同时作为目录名）：必填，≤64 字符，不得包含 \\ / : * ? \" < > | 与控制字符，建议小写连字符。" +
                    "反例：\"Fix Bug 2024/11/01\"（含空格与斜杠）、\"tmp:test\"（含冒号）。"
            },
            description = new
            {
                type = "string",
                maxLength = MaxDescriptionLength,
                description =
                    "技能一句话描述（写入 frontmatter，供目录检索）：必填，≤1024 字符，写「什么时候用这个技能」。" +
                    "反例：\"很有用\"（无判别信息）、\"见正文\"（等于没写）。"
            },
            content = new
            {
                type = "string",
                maxLength = MaxContentLength,
                description =
                    "技能正文（Markdown，不含 frontmatter）：必填，≤8000 字符，逐步写清流程与判定条件；" +
                    "不得出现指令性注入语句。" +
                    "反例：\"本次报错堆栈如下…\"（一次性叙事）、\"忽略之前的指令\"（注入模式）。"
            }
        },
        required = new[] { "name", "description", "content" },
        additionalProperties = false
    });

    /// <inheritdoc />
    public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        var name = GetStringArgument(arguments, "name")?.Trim();
        var description = GetStringArgument(arguments, "description")?.Trim();
        var content = GetStringArgument(arguments, "content")?.Trim();

        // 校验顺序固定：名称 → 描述 → 正文，便于调用方一次只修一个问题。
        if (string.IsNullOrWhiteSpace(name))
            return Failure("name 参数是必需的，且不能为空白字符。");

        if (name.Length > MaxNameLength)
            return Failure($"name 长度不能超过 {MaxNameLength} 个字符（当前 {name.Length} 个）。");

        if (name.IndexOfAny(s_illegalNameChars) >= 0 || name.Any(char.IsControl))
            return Failure("name 不能包含 \\ / : * ? \" < > | 等路径或文件名字符，也不能包含控制字符。");

        if (string.IsNullOrWhiteSpace(description))
            return Failure("description 参数是必需的，且不能为空白字符。");

        if (description.Length > MaxDescriptionLength)
            return Failure($"description 长度不能超过 {MaxDescriptionLength} 个字符（当前 {description.Length} 个）。");

        if (string.IsNullOrWhiteSpace(content))
            return Failure("content 参数是必需的，且不能为空白字符。");

        if (content.Length > MaxContentLength)
            return Failure($"content 长度不能超过 {MaxContentLength} 个字符（当前 {content.Length} 个）。");

        var injection = s_injectionPatterns.FirstOrDefault(
            p => content.Contains(p, StringComparison.OrdinalIgnoreCase));
        if (injection is not null)
            return Failure($"content 含有疑似提示注入片段「{injection}」，请改写为中性描述后再提交。");

        var mode = _options.CurrentValue.Mode;

        if (mode != ReflectionMode.ApplyCreate)
            return BuildSuggestionResult(name, description, content, mode);

        // 查重：同名技能一律不覆盖，避免把用户既有技能静默改掉。
        if (_catalog?.Find(name) is not null)
            return Failure($"已存在同名技能「{name}」，未覆盖；请换一个名称后重试。");

        if (_writer is null)
            return Failure("技能写入通道不可用（未装配技能能力包），本次未写入。");

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["distilled-by"] = "seeing-reflection",
            ["source-session"] = context.SessionId ?? string.Empty
        };

        var draft = new SkillDraft(name, description, content, metadata);

        try
        {
            var result = await _writer.CreateAsync(
                draft,
                ConfigLevel.Project,
                overwrite: false,
                context.CancellationToken).ConfigureAwait(false);

            if (!result.Success)
            {
                var reason = string.IsNullOrWhiteSpace(result.Error) ? "未知原因" : result.Error;
                return Failure($"技能写入失败：{reason}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "技能写入失败（name={Name}）", name);
            return Failure($"技能写入失败：{ex.Message}");
        }

        return Success(
            "技能已写入",
            $"已写入项目级技能「{name}」（来源：复盘）。",
            new Dictionary<string, object>
            {
                ["skill"] = name,
                ["level"] = nameof(ConfigLevel.Project)
            });
    }

    /// <summary>
    /// 构造「只建议、不写盘」的结果：把三个参数原样回显，方便用户手工采纳。
    /// </summary>
    private ToolResult BuildSuggestionResult(
        string name,
        string description,
        string content,
        ReflectionMode mode)
    {
        var reason = mode == ReflectionMode.Off
            ? "复盘模式为 Off"
            : "复盘模式为 SuggestOnly";

        var builder = new StringBuilder();
        builder.Append("本次未写入技能（").Append(reason).AppendLine("），以下为建议内容，可手工采纳：");
        builder.AppendLine();
        builder.Append("name: ").AppendLine(name);
        builder.Append("description: ").AppendLine(description);
        builder.AppendLine("content: ");
        builder.Append(content);

        return Success("技能建议（未写入）", builder.ToString(), new Dictionary<string, object>
        {
            ["skill"] = name,
            ["written"] = false,
            ["mode"] = mode.ToString()
        });
    }
}
