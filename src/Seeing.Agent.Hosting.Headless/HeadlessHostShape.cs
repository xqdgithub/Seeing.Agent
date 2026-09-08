namespace Seeing.Agent.Hosting.Headless;

/// <summary>
/// Host Shape 描述：声明本形态可承载的模块类型 id（字符串，非 ProjectReference）。
/// </summary>
public sealed class HostShapeDescriptor
{
    public required string Id { get; init; }
    public required IReadOnlyList<string> CarriableModuleTypes { get; init; }
}

/// <summary>Headless Host Shape 常量与默认描述符。</summary>
public static class HeadlessHostShape
{
    public const string Id = "headless";

    /// <summary>
    /// 无 UI 形态可承载的模块类型（不含 ui 贡献）。实际启用集由 sample 引用 + Scenario 结算决定。
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
        "skills",
        "mcp",
        "memory",
        "scheduler",
        "acp",
        "token-budget",
        "gateway",
    ];

    public static HostShapeDescriptor Descriptor { get; } = new()
    {
        Id = Id,
        CarriableModuleTypes = CarriableModuleTypes,
    };
}
