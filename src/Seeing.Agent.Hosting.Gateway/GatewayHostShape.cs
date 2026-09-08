namespace Seeing.Agent.Hosting.Gateway;

/// <summary>
/// Host Shape 描述：声明本形态可承载的模块类型 id（字符串，非 ProjectReference）。
/// </summary>
public sealed class HostShapeDescriptor
{
    public required string Id { get; init; }
    public required IReadOnlyList<string> CarriableModuleTypes { get; init; }
}

/// <summary>Gateway Host Shape 常量与默认描述符。</summary>
public static class GatewayHostShape
{
    public const string Id = "gateway";

    /// <summary>
    /// Gateway 服务端形态可承载的模块类型（无 Blazor UI 贡献）。
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
