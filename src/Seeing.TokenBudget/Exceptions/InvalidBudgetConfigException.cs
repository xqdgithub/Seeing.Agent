namespace Seeing.TokenBudget.Exceptions;

public class InvalidBudgetConfigException : Exception
{
    public InvalidBudgetConfigException(string message) : base(message) { }
}
