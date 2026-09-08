using Seeing.Agent.Memory.Abstractions;

namespace Seeing.Agent.Memory.Abstractions;

/// <summary>会话结束事件（sessionId 流）。</summary>
public interface IMemorySessionEvents
{
    IObservable<string> SessionEnded { get; }
}
