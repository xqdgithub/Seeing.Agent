namespace Seeing.Agent.Hosting.Tui;

/// <summary>
/// Host Shape 描述：声明本形态可承载的模块类型 id（字符串，非 ProjectReference）。
/// </summary>
public sealed class HostShapeDescriptor
{
    public required string Id { get; init; }
    public required IReadOnlyList<string> CarriableModuleTypes { get; init; }

    /// <summary>未配置 seeing.json scenario 时的进程级默认场景。</summary>
    public string? DefaultScenario { get; init; }
}

/// <summary>
/// Tui Host Shape 常量与默认描述符。
/// </summary>
public static class TuiHostShape
{
    public const string Id = "tui";

    /// <summary>
    /// 逐字沿用 WebHostShape 的既有字符串约定（tools.* / provider.* / token-budget），去掉 gateway 与 ui。
    /// 该属性全仓无运行时读取方，仅作形态自描述；实施时以 sample 实际注册集为准。
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
    ];

    public static HostShapeDescriptor Descriptor { get; } = new()
    {
        Id = Id,
        CarriableModuleTypes = CarriableModuleTypes,
        DefaultScenario = "full",
    };
}
