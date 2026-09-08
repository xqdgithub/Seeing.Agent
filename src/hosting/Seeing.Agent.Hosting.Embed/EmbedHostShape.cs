namespace Seeing.Agent.Hosting.Embed;

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

/// <summary>Embed Host Shape 常量与默认描述符（进程内嵌入、无 UI）。</summary>
public static class EmbedHostShape
{
    public const string Id = "embed";

    /// <summary>
    /// 进程内嵌入形态可承载的模块类型（不含 ui / gateway 服务端表面）。
    /// 实际启用集由宿主引用 + Scenario 结算决定；默认场景为 <c>code</c>。
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
    ];

    public static HostShapeDescriptor Descriptor { get; } = new()
    {
        Id = Id,
        CarriableModuleTypes = CarriableModuleTypes,
        DefaultScenario = "code",
    };
}
