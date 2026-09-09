using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Provider.OpenCodeZen;

/// <summary>
/// OpenCode Zen 出站拦截器：按会话注入 x-opencode-session（Console 免费层必需）。
/// </summary>
public sealed class OpenCodeZenCallInterceptor : ILlmCallInterceptor
{
    private readonly string _fallbackSessionId = "ses_" + Guid.NewGuid().ToString("N");

    public int Order => 100;

    public bool AppliesTo(string providerId, string providerType)
        => string.Equals(providerId, "opencode-zen", StringComparison.OrdinalIgnoreCase);

    public void OnSending(LlmOutboundRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sessionId = request.Call?.SessionId?.Trim();
        request.Headers["x-opencode-session"] =
            string.IsNullOrEmpty(sessionId) ? _fallbackSessionId : sessionId;
    }
}
