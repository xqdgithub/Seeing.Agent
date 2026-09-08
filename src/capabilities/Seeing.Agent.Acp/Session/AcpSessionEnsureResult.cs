using Acp.Messages;

namespace Seeing.Agent.Acp.Session;

/// <summary>
/// ACP session/new 或 session/load 后的会话信息。
/// </summary>
public sealed record AcpSessionEnsureResult(
    string SessionId,
    IReadOnlyList<SessionConfigOption> ConfigOptions);
