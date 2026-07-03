namespace Seeing.Agent.Acp.Configuration;

/// <summary>
/// ACP 包级配置辅助（全局配置类型见 <see cref="Seeing.Agent.Configuration.AcpOptions"/>）。
/// </summary>
public static class AcpOptionsDefaults
{
    public const string ConfigurationSection = "SeeingAgent:Acp";

    public static TimeSpan DefaultRequestTimeout => TimeSpan.FromMinutes(5);

    public static TimeSpan DefaultIdleTimeout => TimeSpan.FromMinutes(30);

    public static TimeSpan DefaultStartTimeout => TimeSpan.FromSeconds(30);

    public static TimeSpan DefaultStopTimeout => TimeSpan.FromSeconds(10);
}
