namespace Seeing.Agent.TokenBudget.Exceptions;

public class TokenBudgetException : Exception
{
    public string SessionId { get; }
    public int CurrentTokens { get; }
    public int MaxTokens { get; }
    
    public TokenBudgetException(string sessionId, int current, int max, string message)
        : base(message)
    {
        SessionId = sessionId;
        CurrentTokens = current;
        MaxTokens = max;
    }
}
