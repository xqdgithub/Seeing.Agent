using System.Text.Json;

namespace Seeing.Agent.Cli.Services;

/// <summary>
/// Gateway 启动前置配置读取。
/// <para>
/// <c>GatewayOptions</c>（<c>SeeingAgent:Gateway</c>）以 <c>ConfigScope.ProjectOnly</c> 注册，
/// 只从项目级 <c>&lt;workspace&gt;/.seeing/seeing.json</c> 读取——<c>AddSeeingGatewayServer(registry, IConfiguration)</c>
/// 的 configuration 参数被显式忽略，环境变量与命令行均无法覆盖，且 <c>Enabled</c> 默认为 false。
/// 因此 CLI 必须在启动前探测该节，避免空等就绪后把刚起的进程杀掉。
/// </para>
/// </summary>
internal static class GatewayConfig
{
    public const int DefaultPort = 8765;
    public const string RelativeConfigPath = ".seeing/seeing.json";

    internal static string ResolveConfigPath(string workspaceRoot)
        => Path.Combine(workspaceRoot, ".seeing", "seeing.json");

    /// <summary>读取 <c>SeeingAgent:Gateway</c> 的启用状态与端口；缺失或非法时回落到 (false, 8765)。</summary>
    public static (bool Enabled, int Port) Resolve(string workspaceRoot)
    {
        var path = ResolveConfigPath(workspaceRoot);
        if (!File.Exists(path))
            return (false, DefaultPort);

        try
        {
            using var document = JsonDocument.Parse(
                File.ReadAllText(path),
                new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });

            if (!TryGetProperty(document.RootElement, "SeeingAgent", out var seeingAgent)
                || !TryGetProperty(seeingAgent, "Gateway", out var gateway))
            {
                return (false, DefaultPort);
            }

            var enabled = TryGetProperty(gateway, "Enabled", out var enabledElement)
                && enabledElement.ValueKind == JsonValueKind.True;

            var port = TryGetProperty(gateway, "Port", out var portElement)
                && portElement.ValueKind == JsonValueKind.Number
                && portElement.TryGetInt32(out var parsed)
                && parsed is > 0 and <= 65535
                    ? parsed
                    : DefaultPort;

            return (enabled, port);
        }
        catch
        {
            return (false, DefaultPort);
        }
    }

    /// <summary>给出可直接照做的启用指引。</summary>
    public static string BuildDisabledMessage(string workspaceRoot)
        => $"Gateway 未启用：请在 {ResolveConfigPath(workspaceRoot)} 中设置 " +
           "\"SeeingAgent\": { \"Gateway\": { \"Enabled\": true } } 后重试" +
           "（Gateway 配置仅从项目级 seeing.json 读取，环境变量与命令行参数均不生效）。";

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(name, out value))
                return true;

            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}
