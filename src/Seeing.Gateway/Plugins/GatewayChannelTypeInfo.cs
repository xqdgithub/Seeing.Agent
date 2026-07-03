using Seeing.Gateway.Configuration;

namespace Seeing.Gateway.Plugins;

/// <summary>
/// Gateway Channel 类型信息（UI 发现用）
/// </summary>
public sealed record GatewayChannelTypeInfo(
    string ChannelId,
    string DisplayName,
    string Description,
    bool IsBuiltin,
    string PluginSpec,
    string AssemblyPath,
    string OptionsSectionName,
    Type OptionsType,
    IReadOnlyList<ConfigFieldSchema> Fields,
    Type? ConfigFormComponentType);
