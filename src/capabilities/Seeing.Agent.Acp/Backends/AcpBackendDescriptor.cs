namespace Seeing.Agent.Acp.Backends;

/// <summary>
/// 已解析的 ACP 后端描述符。
/// </summary>
public sealed class AcpBackendDescriptor
{
    public required string Id { get; init; }

    public bool Enabled { get; init; } = true;

    public required string Command { get; init; }

    public IReadOnlyList<string> Args { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>();

    public string? WorkingDirectory { get; init; }

    public string? AuthMethodId { get; init; }
}
