namespace Seeing.Agent.TokenBudget.Exceptions;

public class CompactionFailedException : TokenBudgetException
{
    public string Strategy { get; }
    
    public CompactionFailedException(string sessionId, string strategy, string reason)
        : base(sessionId, 0, 0, 
            $"Compaction failed using {strategy} strategy: {reason}")
    {
        Strategy = strategy;
    }
}
