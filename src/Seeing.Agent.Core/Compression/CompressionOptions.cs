namespace Seeing.Agent.Compression;

/// <summary>
/// 压缩选项
/// </summary>
public sealed class CompressionOptions
{
    /// <summary>
    /// 摘要输出 token 上限（null = 不限制，避免摘要被截断导致压缩不完整；显式配置后生效）
    /// </summary>
    public int? SummaryTargetTokens { get; set; } = null;
}