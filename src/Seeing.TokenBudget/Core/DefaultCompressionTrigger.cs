namespace Seeing.TokenBudget;

/// <summary>
/// Default implementation of compression trigger with dual-threshold logic.
/// </summary>
public class DefaultCompressionTrigger : ICompressionTrigger
{
    /// <inheritdoc />
    public CompressionDecision ShouldTrigger(BudgetStatus status)
    {
        return status.Level switch
        {
            BudgetLevel.Normal => CreateNormalDecision(),
            BudgetLevel.Warning => CreateWarningDecision(status),
            BudgetLevel.Critical => CreateCriticalDecision(status),
            BudgetLevel.Overflow => CreateOverflowDecision(status),
            _ => CompressionDecision.NoCompression
        };
    }

    private static CompressionDecision CreateNormalDecision()
    {
        return CompressionDecision.NoCompression;
    }

    private static CompressionDecision CreateWarningDecision(BudgetStatus status)
    {
        // Warning level: No compression, just a notification
        return new CompressionDecision
        {
            NeedsCompression = false,
            Reason = CompressionReason.ApproachingLimit,
            Immediate = false,
            Message = string.Format("Approaching token limit: {0:F0}% used ({1}/{2} tokens)", 
                status.UsagePercentage, status.CurrentTokens, status.MaxTokens)
        };
    }

    private static CompressionDecision CreateCriticalDecision(BudgetStatus status)
    {
        // Critical level: Needs compression, but can be deferred
        return new CompressionDecision
        {
            NeedsCompression = true,
            Reason = CompressionReason.OverThreshold,
            Immediate = false,
            Message = string.Format("Critical token usage: {0:F0}% used ({1}/{2} tokens). Compression recommended.", 
                status.UsagePercentage, status.CurrentTokens, status.MaxTokens)
        };
    }

    private static CompressionDecision CreateOverflowDecision(BudgetStatus status)
    {
        // Overflow level: Immediate compression required
        return new CompressionDecision
        {
            NeedsCompression = true,
            Reason = CompressionReason.OverMaxLimit,
            Immediate = true,
            Message = string.Format("Token budget exceeded: {0}/{1} tokens ({2:F0}%). Immediate compression required.", 
                status.CurrentTokens, status.MaxTokens, status.UsagePercentage)
        };
    }
}