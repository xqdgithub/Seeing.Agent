using Seeing.Session.Core;
using Seeing.TokenEstimation;

namespace Seeing.Session.Compression;

internal static class TokenCounterHelper
{
    public static int CountTokens(IReadOnlyList<SessionMessage> messages, ITokenCounter counter)
    {
        if (messages == null) return 0;
        var total = 0;
        foreach (var message in messages)
        {
            total += counter.Estimate(message.Content ?? string.Empty);
            if (!string.IsNullOrEmpty(message.ReasoningContent))
                total += counter.Estimate(message.ReasoningContent);
            if (message.ToolCalls != null)
                foreach (var tc in message.ToolCalls)
                {
                    total += counter.Estimate(tc.Name ?? string.Empty);
                    total += counter.Estimate(tc.Arguments ?? string.Empty);
                }
            if (message.Parts != null)
                foreach (var p in message.Parts)
                    if (!string.IsNullOrEmpty(p.Text))
                        total += counter.Estimate(p.Text);
        }
        return total;
    }
}
