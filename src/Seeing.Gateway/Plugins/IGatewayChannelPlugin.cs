using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Seeing.Gateway.Configuration;

namespace Seeing.Gateway.Plugins;

/// <summary>
/// Gateway Channel 插件契约（Bridge 实现 + 配置元数据）
/// </summary>
public interface IGatewayChannelPlugin
{
    string ChannelId { get; }

    string DisplayName { get; }

    string Description { get; }

    bool IsBuiltin { get; }

    Type OptionsType { get; }

    string OptionsSectionName { get; }

    IReadOnlyList<ConfigFieldSchema>? GetConfigSchema() => null;

    Type? ConfigFormComponentType => null;

    void ConfigureServices(IServiceCollection services, IConfiguration configuration);
}
