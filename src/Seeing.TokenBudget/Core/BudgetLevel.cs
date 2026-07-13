namespace Seeing.TokenBudget;

/// <summary>
/// Represents the budget level based on token usage.
/// </summary>
public enum BudgetLevel
{
    /// <summary>
    /// Normal operation - token usage is within safe limits.
    /// </summary>
    Normal,

    /// <summary>
    /// Warning level - approaching capacity limits.
    /// </summary>
    Warning,

    /// <summary>
    /// Critical level - action required to prevent overflow.
    /// </summary>
    Critical,

    /// <summary>
    /// Overflow level - token budget exceeded.
    /// </summary>
    Overflow
}