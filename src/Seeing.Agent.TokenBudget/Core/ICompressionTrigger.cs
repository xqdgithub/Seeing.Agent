using Seeing.Session.Core;

namespace Seeing.Agent.TokenBudget;

/// <summary>
/// Interface for determining when compression should be triggered based on budget status.
/// </summary>
public interface ICompressionTrigger
{
    /// <summary>
    /// Determines whether compression should be triggered based on the current budget status.
    /// </summary>
    /// <param name="status">The current budget status.</param>
    /// <returns>A compression decision indicating whether and how to compress.</returns>
    CompressionDecision ShouldTrigger(BudgetStatus status);
}