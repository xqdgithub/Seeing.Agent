using Acp.Types;

namespace Seeing.Agent.Acp.Execution;

/// <summary>
/// 接收 ACP SessionUpdate 的 Sink。
/// </summary>
public interface IAcpUpdateSink
{
    Task OnSessionUpdateAsync(string acpSessionId, SessionUpdate update, CancellationToken cancellationToken = default);
}
