using Acp.Types;
using AcpMcpServerConfig = Acp.Types.McpServerConfig;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Acp.Backends;
using SeeingMcpConfig = Seeing.Agent.MCP.McpServerConfig;
using SeeingMcpTransportType = Seeing.Agent.MCP.McpTransportType;

namespace Seeing.Agent.Acp.Mapping;

/// <summary>
/// Seeing MCP → ACP MCP 映射规则（OpenCode 仅支持 stdio 与 type=sse）。
/// </summary>
public static class AcpMcpServerMapping
{
    public static AcpMcpServerConfig? TryMap(string name, SeeingMcpConfig config, ILogger logger)
    {
        if (config.Disabled)
        {
            logger.LogDebug("Skipping disabled MCP server '{Name}' for ACP session", name);
            return null;
        }

        return config.TransportType switch
        {
            SeeingMcpTransportType.Stdio => MapStdio(name, config, logger),
            SeeingMcpTransportType.Sse => MapSse(name, config, logger),
            SeeingMcpTransportType.StreamableHttp => Skip(
                logger,
                name,
                "OpenCode ACP session/new does not support streamable HTTP (configure MCP in the ACP agent instead)"),
            _ => Skip(logger, name, $"unsupported transport {config.TransportType} for ACP session")
        };
    }

    private static AcpMcpServerConfig? MapStdio(string name, SeeingMcpConfig config, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(config.Command))
        {
            logger.LogWarning("Skipping MCP server '{Name}': stdio command is missing", name);
            return null;
        }

        return new StdioMcpServer
        {
            Name = name,
            Command = AcpExecutableResolver.Resolve(config.Command),
            Args = config.Args ?? [],
            Env = MapEnv(config.Env)
        };
    }

    private static AcpMcpServerConfig? MapSse(string name, SeeingMcpConfig config, ILogger logger)
    {
        if (config.Url == null)
        {
            logger.LogWarning("Skipping MCP server '{Name}': SSE url is missing", name);
            return null;
        }

        return new SseMcpServer
        {
            Name = name,
            Url = config.Url.ToString()!,
            Headers = MapHeaders(config.Headers)
        };
    }

    private static AcpMcpServerConfig? Skip(ILogger logger, string name, string reason)
    {
        logger.LogDebug("Skipping MCP server '{Name}': {Reason}", name, reason);
        return null;
    }

    private static List<EnvVariable> MapEnv(Dictionary<string, string>? env)
    {
        if (env == null || env.Count == 0)
            return [];

        return env.Select(kv => new EnvVariable
        {
            Name = kv.Key,
            Value = kv.Value
        }).ToList();
    }

    private static List<HttpHeader> MapHeaders(Dictionary<string, string>? headers)
    {
        if (headers == null || headers.Count == 0)
            return [];

        return headers.Select(kv => new HttpHeader
        {
            Name = kv.Key,
            Value = kv.Value
        }).ToList();
    }
}
