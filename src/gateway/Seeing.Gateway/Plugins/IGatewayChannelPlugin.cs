using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Seeing.ConfigSchema;

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

    /// <summary>初始化通道插件</summary>
    Task InitializeAsync(IServiceProvider services) => Task.CompletedTask;

    /// <summary>释放通道插件资源</summary>
    Task DisposeAsync() => Task.CompletedTask;
}
