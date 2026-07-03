using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Seeing.Gateway.Client.Extensions;
using Seeing.Gateway.Configuration;
using Seeing.Gateway.Plugins;
using Seeing.Gateway.WeCom.Extensions;

namespace Seeing.Gateway.WeCom;

/// <summary>
/// 企业微信 Channel 内置插件
/// </summary>
public sealed class WeComChannelPlugin : IGatewayChannelPlugin
{
    public string ChannelId => "wecom";

    public string DisplayName => "企业微信";

    public string Description => "企业微信 AI Bot WebSocket 通道桥接";

    public bool IsBuiltin => true;

    public Type OptionsType => typeof(WeComOptions);

    public string OptionsSectionName => WeComOptions.SectionName;

    public IReadOnlyList<ConfigFieldSchema>? GetConfigSchema() => OptionsSchemaBuilder.FromType(typeof(WeComOptions));

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<WeComOptions>(configuration.GetSection(WeComOptions.SectionName));
        services.AddSeeingWeComChannel();
        services.ConfigureGatewayClientCommon(configuration);
    }
}
