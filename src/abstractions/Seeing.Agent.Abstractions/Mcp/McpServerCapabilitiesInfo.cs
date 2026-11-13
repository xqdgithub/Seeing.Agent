namespace Seeing.Agent.Abstractions.Mcp;

/// <summary>
/// 单个 MCP server 在握手后声明的能力摘要（供状态投影 / UI / 管理工具共用）。
/// </summary>
public sealed record McpServerCapabilitiesInfo(
    bool Tools,
    bool Resources,
    bool ResourcesSubscribe,
    bool ResourcesListChanged,
    bool Prompts,
    bool PromptsListChanged,
    bool SkillsExtension,
    bool SkillsDirectoryRead);
