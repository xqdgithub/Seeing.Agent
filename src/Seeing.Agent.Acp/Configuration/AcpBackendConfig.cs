namespace Seeing.Agent.Acp.Configuration;

/// <summary>
/// ACP 后端配置扩展（基础字段见 <see cref="Seeing.Agent.Configuration.AcpBackendConfig"/>）。
/// </summary>
public sealed class AcpBackendSettings
{
    /// <summary>后端标识</summary>
    public required string Id { get; init; }

    /// <summary>是否启用</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>启动命令</summary>
    public required string Command { get; init; }

    /// <summary>命令参数</summary>
    public IReadOnlyList<string> Args { get; init; } = Array.Empty<string>();

    /// <summary>环境变量</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>();

    /// <summary>默认工作目录</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>认证方法 ID（可选）</summary>
    public string? AuthMethodId { get; init; }

    public static AcpBackendSettings FromCore(string id, CoreAcpBackendConfig config, bool enabled = true) =>
        new()
        {
            Id = id,
            Enabled = enabled,
            Command = config.Command ?? throw new InvalidOperationException($"Backend '{id}' missing Command"),
            Args = config.Args ?? new List<string>(),
            Environment = config.Environment ?? new Dictionary<string, string>()
        };
}
