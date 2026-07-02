using Seeing.Agent.Llm;

namespace Seeing.Gateway.Models;

/// <summary>
/// Token 使用统计（Gateway 协议 DTO）
/// </summary>
public record GatewayTokenUsage
{
    public int InputTokens { get; init; }

    public int OutputTokens { get; init; }

    public int TotalTokens => InputTokens + OutputTokens;

    public static GatewayTokenUsage? FromTokenUsage(TokenUsage? usage)
    {
        if (usage == null)
            return null;

        return new GatewayTokenUsage
        {
            InputTokens = usage.InputTokens,
            OutputTokens = usage.OutputTokens
        };
    }
}
