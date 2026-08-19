namespace Seeing.Agent.Compression;

/// <summary>
/// 压缩执行结果
/// </summary>
public sealed class CompressionOutcome
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public int TokensBefore { get; init; }
    public int TokensAfter { get; init; }
    public int MessagesRemoved { get; init; }
    public string? Summary { get; init; }
    public string? Strategy { get; init; }
}