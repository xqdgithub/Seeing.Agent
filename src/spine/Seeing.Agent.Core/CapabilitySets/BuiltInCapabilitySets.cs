namespace Seeing.Agent.Core.CapabilitySets;

/// <summary>
/// 内置能力集（boot 模块列表 SSOT）。<see cref="Scenarios.BuiltInScenarios"/> 编译期引用本类常量，禁止复制数组。
/// </summary>
public static class BuiltInCapabilitySets
{
    private static readonly IReadOnlyList<string> s_noDisabled = Array.Empty<string>();

    private static readonly IReadOnlyList<string> s_providerModules =
    [
        "provider.deepseek",
        "provider.opencodezen",
    ];

    /// <summary>最小启动能力集模块列表。</summary>
    public static IReadOnlyList<string> Minimal { get; } =
    [
        "io.local",
        "agents.builtin",
        "llm.openai",
        "basic",
        ..s_providerModules,
    ];

    /// <summary>代码工作能力集模块列表。</summary>
    public static IReadOnlyList<string> Code { get; } =
    [
        "io.local",
        "agents.builtin",
        "llm.openai",
        "basic",
        "filesystem",
        "shell",
        "git",
        "subagent",
        "session.tools",
        "question.tools",
        ..s_providerModules,
    ];

    /// <summary>办公协作能力集模块列表。</summary>
    public static IReadOnlyList<string> Work { get; } =
    [
        "io.local",
        "agents.builtin",
        "llm.openai",
        "basic",
        "filesystem",
        "web",
        "memory",
        "scheduler",
        "question.tools",
        ..s_providerModules,
    ];

    /// <summary>研究探索能力集模块列表。</summary>
    public static IReadOnlyList<string> Research { get; } =
    [
        "io.local",
        "agents.builtin",
        "llm.openai",
        "basic",
        "web",
        "memory",
        "question.tools",
        ..s_providerModules,
    ];

    /// <summary>
    /// 全量能力集：当前仓库已实现的全部 <c>ISeeingModule</c> id（字符串，不引用能力包类型），
    /// 但显式排除 <see cref="FullExcludedModules"/> 中的可选在线外部源。
    /// </summary>
    public static IReadOnlyList<string> Full { get; } =
    [
        "io.local",
        "agents.builtin",
        "llm.openai",
        "llm.anthropic",
        "llm.modelcapabilities",
        "llm.modelcatalog.builtin",
        "basic",
        "filesystem",
        "shell",
        "git",
        "subagent",
        "session.tools",
        "question.tools",
        "web",
        "memory",
        "scheduler",
        "skills",
        "mcp",
        "acp",
        "gateway",
        "systemone",
        "systemone.tools",
        ..s_providerModules,
    ];

    /// <summary>
    /// 有意不纳入 <see cref="Full"/> 的已实现模块 id（反射守门测试的显式豁免清单 SSOT）。
    /// </summary>
    /// <remarks>
    /// <c>llm.modelcatalog.modelsdev</c>：models.dev 在线外部模型目录源。其 <c>ActivateAsync</c>
    /// 仅加载本地/嵌入目录、不发起网络请求；但 <c>RefreshAsync</c> 会访问 models.dev（失败降级为保留本地目录并事件通知）。
    /// 作为带网络副作用的可选外部源，不适合默认随 Full/Dev 启用——须由宿主显式 <c>AddSeeingModule</c> 登记
    /// （WebUI 即采用此可选策略，设置页注明"models.dev 全量源默认不加载"）。
    /// 新增豁免项须同步评估其默认启用的副作用。
    /// </remarks>
    public static IReadOnlyList<string> FullExcludedModules { get; } =
    [
        "llm.modelcatalog.modelsdev",
    ];

    /// <summary>最小档能力集定义（仅核心模块集 Minimal）。</summary>
    public static CapabilitySetDefinition MinimalSet { get; } = new(
        Name: "minimal",
        Modules: Minimal,
        Disabled: s_noDisabled);

    /// <summary>编码档能力集定义（模块集 Code）。</summary>
    public static CapabilitySetDefinition CodeSet { get; } = new(
        Name: "code",
        Modules: Code,
        Disabled: s_noDisabled);

    /// <summary>办公档能力集定义（模块集 Work）。</summary>
    public static CapabilitySetDefinition WorkSet { get; } = new(
        Name: "work",
        Modules: Work,
        Disabled: s_noDisabled);

    /// <summary>研究档能力集定义（模块集 Research）。</summary>
    public static CapabilitySetDefinition ResearchSet { get; } = new(
        Name: "research",
        Modules: Research,
        Disabled: s_noDisabled);

    /// <summary>全功能档能力集定义（模块集 Full）。</summary>
    public static CapabilitySetDefinition FullSet { get; } = new(
        Name: "full",
        Modules: Full,
        Disabled: s_noDisabled);

    /// <summary>
    /// 安全档：全 Available，禁用高风险模块（shell / mcp / acp / gateway）。
    /// </summary>
    public static IReadOnlyList<string> SecureModules { get; } = ["*"];

    /// <summary>安全档禁用列表（模块层）。</summary>
    public static IReadOnlyList<string> SecureDisabled { get; } =
    [
        "shell",
        "mcp",
        "acp",
        "gateway",
    ];

    /// <summary>开发档模块列表（与 <see cref="Full"/> 同一引用）。</summary>
    public static IReadOnlyList<string> Dev => Full;

    /// <summary>安全档能力集定义（禁用 shell/mcp/acp/gateway 等高风险模块）。</summary>
    public static CapabilitySetDefinition SecureSet { get; } = new(
        Name: "secure",
        Modules: SecureModules,
        Disabled: SecureDisabled);

    /// <summary>开发档能力集定义（模块集与全功能档一致）。</summary>
    public static CapabilitySetDefinition DevSet { get; } = new(
        Name: "dev",
        Modules: Dev,
        Disabled: s_noDisabled);

    /// <summary>按名称查找内置能力集；未知名称返回 null。</summary>
    public static CapabilitySetDefinition? TryGet(string name) => name switch
    {
        "minimal" => MinimalSet,
        "code" => CodeSet,
        "work" => WorkSet,
        "research" => ResearchSet,
        "full" => FullSet,
        "secure" => SecureSet,
        "dev" => DevSet,
        _ => null,
    };

    /// <summary>全部内置能力集（稳定顺序）。</summary>
    public static IReadOnlyList<CapabilitySetDefinition> All { get; } =
    [
        MinimalSet,
        CodeSet,
        WorkSet,
        ResearchSet,
        FullSet,
        SecureSet,
        DevSet,
    ];
}
