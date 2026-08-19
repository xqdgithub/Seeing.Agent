namespace Seeing.Agent.Cli.Services;

/// <summary>
/// WebUI 的监听地址生成规则。启动参数、环境变量和就绪检查必须使用同一个地址。
/// </summary>
internal static class WebUiLaunch
{
    public const int PreferredPort = 5000;

    public static string BuildUrl(int port)
        => $"http://127.0.0.1:{port}";

    public static string[] BuildArguments(int port)
        => new[] { "--urls", BuildUrl(port) };

    public static Dictionary<string, string?> BuildEnvironment(int port)
    {
        var url = BuildUrl(port);
        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ASPNETCORE_URLS"] = url,
            ["DOTNET_URLS"] = url
        };
    }
}
