namespace Seeing.Agent.TokenBudget.Exceptions;

public class ContextOverflowException : TokenBudgetException
{
    public ContextOverflowException(string sessionId, int current, int max)
        : base(sessionId, current, max, 
            $"Context overflow: {current} tokens exceed maximum {max}")
    {
    }
}
