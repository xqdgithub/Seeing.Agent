using Microsoft.Extensions.Logging;

namespace Seeing.TokenBudget.Logging;

public static class TokenBudgetLoggerExtensions
{
    private static readonly Action<ILogger, string, int, int, double, Exception?> _budgetCheck =
        LoggerMessage.Define<string, int, int, double>(
            LogLevel.Debug,
            TokenBudgetLogEvents.BudgetCheck,
            "[{SessionId}] Budget check: {CurrentTokens}/{MaxTokens} tokens ({Percentage:F1}%)");
    
    private static readonly Action<ILogger, string, int, int, double, Exception?> _budgetWarning =
        LoggerMessage.Define<string, int, int, double>(
            LogLevel.Warning,
            TokenBudgetLogEvents.BudgetWarning,
            "[{SessionId}] Budget warning: {CurrentTokens}/{MaxTokens} tokens ({Percentage:F1}%) - approaching limit");
    
    private static readonly Action<ILogger, string, string, int, int, int, Exception?> _compactionCompleted =
        LoggerMessage.Define<string, string, int, int, int>(
            LogLevel.Information,
            TokenBudgetLogEvents.CompactionCompleted,
            "[{SessionId}] Compaction completed ({Strategy}): {TokensBefore} -> {TokensAfter} tokens ({TokensSaved} saved)");
    
    public static void LogBudgetCheck(this ILogger logger, string sessionId, int current, int max)
        => _budgetCheck(logger, sessionId, current, max, max > 0 ? (double)current / max * 100 : 0, null);
    
    public static void LogBudgetWarning(this ILogger logger, string sessionId, int current, int max)
        => _budgetWarning(logger, sessionId, current, max, max > 0 ? (double)current / max * 100 : 0, null);
    
    public static void LogCompactionCompleted(this ILogger logger, string sessionId, string strategy, int before, int after)
        => _compactionCompleted(logger, sessionId, strategy, before, after, before - after, null);
}
