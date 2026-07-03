namespace Seeing.Gateway.Configuration;

/// <summary>
/// Gateway Channel 配置字段 Schema（驱动 UI 动态表单）
/// </summary>
public sealed record ConfigFieldSchema(
    string Name,
    string Label,
    string? Description,
    ConfigFieldType Type,
    bool Required,
    object? DefaultValue,
    IReadOnlyList<string>? EnumValues = null,
    string? Section = null);
