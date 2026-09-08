namespace Seeing.Agent.Acp.Configuration;

/// <summary>
/// ACP 配置节默认值与节名常量。
/// </summary>
public static class AcpOptionsDefaults
{
    public const string ConfigurationSection = "SeeingAgent:Acp";

    public static TimeSpan DefaultRequestTimeout => TimeSpan.FromMinutes(5);

    public static TimeSpan DefaultIdleTimeout => TimeSpan.FromMinutes(30);

    public static TimeSpan DefaultStartTimeout => TimeSpan.FromSeconds(30);

    public static TimeSpan DefaultStopTimeout => TimeSpan.FromSeconds(10);
}
