using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Seeing.Gateway.Client;
using Seeing.Gateway.Client.Extensions;
using Seeing.Gateway.Configuration;
using Seeing.Gateway.Plugins;
using Seeing.Gateway.QQ.Extensions;

namespace Seeing.Gateway.QQ;

/// <summary>
/// QQ Channel 内置插件
/// </summary>
public sealed class QQChannelPlugin : IGatewayChannelPlugin
{
    public string ChannelId => "qq";

    public string DisplayName => "QQ";

    public string Description => "QQ 官方机器人 WebSocket/HTTP 通道桥接";

    public bool IsBuiltin => true;

    public Type OptionsType => typeof(QQOptions);

    public string OptionsSectionName => QQOptions.SectionName;

    public IReadOnlyList<ConfigFieldSchema>? GetConfigSchema() => OptionsSchemaBuilder.FromType(typeof(QQOptions));

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<QQOptions>(configuration.GetSection(QQOptions.SectionName));
        services.AddSeeingQQChannel();
        services.Configure<GatewayClientOptions>(configuration.GetSection(GatewayClientOptions.SectionName));
        services.ConfigureGatewayClientCommon(configuration);
    }
}
