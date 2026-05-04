namespace Seeing.Agent.MCP.Validation;

using Seeing.Agent.MCP.Core;

public sealed class McpValidationResult
{
    public bool IsValid { get; }
    public McpErrorInfo? Error { get; }
    public IReadOnlyList<string> Warnings { get; }

    private McpValidationResult(bool isValid, McpErrorInfo? error, IReadOnlyList<string> warnings)
    {
        IsValid = isValid;
        Error = error;
        Warnings = warnings;
    }

    public static McpValidationResult Valid() => new(true, null, Array.Empty<string>());
    public static McpValidationResult Invalid(McpErrorInfo error) => new(false, error, Array.Empty<string>());
    public static McpValidationResult WithWarnings(IReadOnlyList<string> warnings) => new(true, null, warnings);
}