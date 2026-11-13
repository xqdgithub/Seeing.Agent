using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using Seeing.Agent.Abstractions.Mcp;

namespace Seeing.Agent.Mcp.Session;

/// <summary>stdio 传输的 MCP 会话实现。</summary>
internal sealed class StdioMcpSession : McpClientSessionBase
{
    private StdioClientTransport? _transport;

    public StdioMcpSession(McpServerConfig config, ILogger logger)
        : base(config, logger)
    {
    }

    protected override async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(Config.Command))
            throw new InvalidOperationException("stdio 传输需要配置 command");

        Logger.LogInformation(
            "正在连接 MCP Server (stdio): {Name}, Command: {Command}, Args: {Args}, Timeout: {Timeout}s",
            Config.Name,
            Config.Command,
            Config.Args is { Count: > 0 } ? string.Join(' ', Config.Args) : "(none)",
            Config.ConnectionTimeout.TotalSeconds);

        var transportOptions = new StdioClientTransportOptions
        {
            Name = Config.Name,
            Command = Config.Command,
            Arguments = Config.Args?.ToArray() ?? Array.Empty<string>(),
            ShutdownTimeout = Config.ShutdownTimeout,
            StandardErrorLines = line =>
            {
                if (!string.IsNullOrWhiteSpace(line))
                    Logger.LogWarning("MCP Server {Name} stderr: {Line}", Config.Name, line);
            }
        };

        if (!string.IsNullOrEmpty(Config.WorkingDirectory))
            transportOptions.WorkingDirectory = Config.WorkingDirectory;

        if (Config.Env is { Count: > 0 })
        {
            transportOptions.EnvironmentVariables = new Dictionary<string, string?>();
            foreach (var env in Config.Env)
                transportOptions.EnvironmentVariables[env.Key] = env.Value;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(Config.ConnectionTimeout);

        try
        {
            _transport = new StdioClientTransport(transportOptions);
            SetClient(await McpClient.CreateAsync(_transport, cancellationToken: timeoutCts.Token)
                .ConfigureAwait(false));
            Logger.LogInformation("MCP Server {Name} 连接成功", Config.Name);
        }
        catch
        {
            SetClient(null);
            _transport = null;
            throw;
        }
    }

    public override async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await base.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        _transport = null;
    }
}
