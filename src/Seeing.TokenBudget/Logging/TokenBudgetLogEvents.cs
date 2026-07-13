using Microsoft.Extensions.Logging;

namespace Seeing.TokenBudget.Logging;

public static class TokenBudgetLogEvents
{
    public static readonly EventId BudgetCheck = new(4001, "BudgetCheck");
    public static readonly EventId BudgetWarning = new(4002, "BudgetWarning");
    public static readonly EventId BudgetCritical = new(4003, "BudgetCritical");
    public static readonly EventId BudgetOverflow = new(4004, "BudgetOverflow");
    public static readonly EventId CompactionTriggered = new(4011, "CompactionTriggered");
    public static readonly EventId CompactionCompleted = new(4012, "CompactionCompleted");
    public static readonly EventId CompactionFailed = new(4013, "CompactionFailed");
}
