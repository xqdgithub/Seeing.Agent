namespace Seeing.Agent.Compression;

/// <summary>
/// 压缩选项 - 映射自 TokenBudgetConfig（保留字段作来源）
/// </summary>
public sealed class CompressionOptions
{
    /// <summary>摘要目标 token 数（默认 4000）</summary>
    public int SummaryTargetTokens { get; set; } = 4000;

    /// <summary>保留最近消息条数（默认 10）</summary>
    public int KeepRecentMessages { get; set; } = 10;
}