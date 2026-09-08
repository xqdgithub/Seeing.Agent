namespace Seeing.Agent.Hosting.Web;

/// <summary>
/// Host Shape 描述：声明本形态可承载的模块类型 id（字符串，非 ProjectReference）。
/// 能力包由宿主 sample 自行引用并登记到模块目录。
/// </summary>
public sealed class HostShapeDescriptor
{
    public required string Id { get; init; }

    /// <summary>
    /// 可承载模块类型（开放登记用字符串 id，如 tools.filesystem / memory / scheduler）。
    /// </summary>
    public required IReadOnlyList<string> CarriableModuleTypes { get; init; }

    /// <summary>未配置 seeing.json scenario 时的进程级默认场景。</summary>
    public string? DefaultScenario { get; init; }
}

/// <summary>Web Host Shape 常量与默认描述符。</summary>
public static class WebHostShape
{
    public const string Id = "web";

    /// <summary>
    /// Web 形态可承载的模块类型（含 UI 贡献）。实际启用集由 sample 引用 + Scenario 结算决定。
    /// </summary>
    public static readonly IReadOnlyList<string> CarriableModuleTypes =
    [
        "tools.basic",
        "tools.filesystem",
        "tools.shell",
        "tools.web",
        "tools.git",
        "llm.openai",
        "llm.anthropic",
        "provider.deepseek",
        "provider.opencodezen",
        "skills",
        "mcp",
        "memory",
        "scheduler",
        "acp",
        "token-budget",
        "gateway",
        "ui",
    ];

    public static HostShapeDescriptor Descriptor { get; } = new()
    {
        Id = Id,
        CarriableModuleTypes = CarriableModuleTypes,
        DefaultScenario = "full",
    };
}
