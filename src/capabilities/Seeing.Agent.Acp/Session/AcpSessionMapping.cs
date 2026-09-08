namespace Seeing.Agent.Acp.Session;

/// <summary>
/// Seeing Session 与 ACP Session 的映射 DTO。
/// </summary>
public sealed class AcpSessionMapping
{
    public required string BackendId { get; init; }

    public required string AcpSessionId { get; init; }

    public string Serialize() => $"{BackendId}|{AcpSessionId}";

    public static AcpSessionMapping? TryParse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var separator = value.IndexOf('|');
        if (separator <= 0 || separator >= value.Length - 1)
            return null;

        var backendId = value[..separator];
        var acpSessionId = value[(separator + 1)..];
        if (string.IsNullOrWhiteSpace(backendId) || string.IsNullOrWhiteSpace(acpSessionId))
            return null;

        return new AcpSessionMapping
        {
            BackendId = backendId,
            AcpSessionId = acpSessionId
        };
    }
}
