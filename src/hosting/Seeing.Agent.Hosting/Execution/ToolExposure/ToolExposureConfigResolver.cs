using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Scenarios;
using Seeing.Session.Core;

namespace Seeing.Agent.Hosting.Execution.ToolExposure;

/// <summary>
/// 工具暴露策略四级解析后的有效配置（会话 &gt; Agent &gt; 场景 &gt; 全局）。
/// 数值参数（预算/阈值）无覆盖级，恒取全局 <c>Tools.Exposure</c>。
/// </summary>
public sealed record EffectiveExposureConfig(
    ToolExposurePolicyMode Mode,
    IReadOnlyList<string> PinnedTools,
    int CatalogBudgetChars,
    int CatalogLineMaxChars,
    int EscalateToolCount,
    int EscalateSchemaTokens);

/// <summary>
/// 工具暴露策略配置解析器：把会话/Agent/场景/全局四级模式收敛为一个有效模式。
/// <para>
/// 纯解析、无 I/O；不触碰 <c>IToolManager</c>/session/store。全局层经构造注入的
/// <see cref="IOptionsMonitor{T}"/>（<see cref="SeeingAgentOptions"/>）读取，供热重载与 DI 消费。
/// </para>
/// <para>
/// 模式各层承载类型不同：会话/场景/全局为 <c>string</c>（跨层统一、ConfigSchema 友好，
/// 原语/配置层不依赖 Abstractions 枚举），Agent 为 <see cref="ToolExposurePolicyMode"/>? 枚举（直接采用）。
/// 字符串层用 <c>Enum.TryParse(ignoreCase)</c> + <c>Enum.IsDefined</c> 双重校验：
/// null/空白视为未配置（静默跳过）；非空但不可解析记 Warning 后回退下一优先级层，绝不抛异常。
/// </para>
/// </summary>
public sealed class ToolExposureConfigResolver
{
    private readonly IOptionsMonitor<SeeingAgentOptions> _options;
    private readonly ILogger<ToolExposureConfigResolver> _logger;

    public ToolExposureConfigResolver(
        IOptionsMonitor<SeeingAgentOptions> options,
        ILogger<ToolExposureConfigResolver>? logger = null)
    {
        _options = options;
        _logger = logger
            ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ToolExposureConfigResolver>.Instance;
    }

    /// <summary>
    /// 默认钉选的核心工具（分层暴露下恒内联）。
    /// <para>
    /// 放在 Hosting 组合层而非 Core 配置初值：既避免"能力策略落进瘦脊柱"，
    /// 也避免非空默认值被 <c>MergeDeep</c> 当作"项目级已设置"而覆盖用户配置。
    /// 用户经 <c>Tools.Exposure.PinnedTools</c> 非空时整体替换；Agent 级同义且优先级更高。
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultPinnedTools =
    [
        "read",
        "write",
        "edit",
        "glob",
        "grep",
        "bash",
        "delete",
        "open_file",
        "add_workspace_path",
        "question",
        "skill",
        "task",
        "todowrite",
        "webfetch",
        "websearch",
        "codesearch",
        "memory_search",
        "memory_read",
        "memory_write"
    ];

    /// <summary>
    /// 解析四级优先级的有效暴露配置。
    /// </summary>
    /// <param name="sessionOverride">会话级覆盖（最高优先级）。</param>
    /// <param name="agent">Agent 定义（枚举层）。</param>
    /// <param name="scenario">已结算场景定义（字符串透传层）。</param>
    /// <param name="globalOptions">
    /// 显式全局选项（可选）；为 null 时经注入的 <see cref="IOptionsMonitor{T}"/> 读取 <c>CurrentValue</c>，
    /// 以便 DI 消费方无需自备全局选项，同时保留测试/特殊路径显式注入。
    /// </param>
    public EffectiveExposureConfig Resolve(
        SessionScenarioOverride? sessionOverride,
        AgentDefinition? agent,
        ScenarioDefinition? scenario,
        SeeingAgentOptions? globalOptions = null)
    {
        var options = globalOptions ?? _options.CurrentValue;
        var exposure = options.Tools?.Exposure ?? new ToolExposureOptions();

        var mode = ResolveMode(sessionOverride, agent, scenario, exposure);

        // PinnedTools 三级：Agent（整体覆盖）→ 配置（非空整体替换默认集）→ 默认集。
        // 默认集放代码而非选项初始化器，避免 MergeDeep 把"未设置"误判为"项目级已设置"。
        IReadOnlyList<string> pinned = agent?.PinnedTools
            ?? (exposure.PinnedTools is { Count: > 0 } ? exposure.PinnedTools : DefaultPinnedTools);

        return new EffectiveExposureConfig(
            mode,
            pinned,
            exposure.CatalogBudgetChars,
            exposure.CatalogLineMaxChars,
            exposure.EscalateToolCount,
            exposure.EscalateSchemaTokens);
    }

    /// <summary>
    /// 四级模式解析：会话(string) → Agent(枚举) → 场景(string) → 全局(string)，
    /// 取第一个「存在且可解析」者；字符串非法记 Warning 后降级，末路兜底 <see cref="ToolExposurePolicyMode.Auto"/>。
    /// </summary>
    private ToolExposurePolicyMode ResolveMode(
        SessionScenarioOverride? sessionOverride,
        AgentDefinition? agent,
        ScenarioDefinition? scenario,
        ToolExposureOptions globalExposure)
    {
        if (TryParseLevel(sessionOverride?.Tools?.PolicyMode, "Session", out var mode))
            return mode;

        // Agent 层本就是枚举，直接采用（无解析失败可能）。
        if (agent?.Exposure is { } agentMode)
            return agentMode;

        if (TryParseLevel(scenario?.PolicyMode, "Scenario", out mode))
            return mode;

        if (TryParseLevel(globalExposure.PolicyMode, "Global", out mode))
            return mode;

        return ToolExposurePolicyMode.Auto;
    }

    /// <summary>
    /// 解析单个字符串层：null/空白→未配置（静默 false，不告警）；可解析且在枚举定义域内→true；
    /// 否则记结构化 Warning 返回 false（由调用方降级到下一优先级层）。
    /// </summary>
    private bool TryParseLevel(string? raw, string level, out ToolExposurePolicyMode mode)
    {
        mode = default;

        if (string.IsNullOrWhiteSpace(raw))
            return false;

        if (Enum.TryParse(raw.Trim(), ignoreCase: true, out ToolExposurePolicyMode parsed)
            && Enum.IsDefined(parsed))
        {
            mode = parsed;
            return true;
        }

        _logger.LogWarning(
            "工具暴露策略 {Level} 层值 '{Value}' 无法解析，已回退到下一优先级层。",
            level,
            raw);

        return false;
    }
}
