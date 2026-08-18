using Seeing.Agent.Abstractions.Hooks;

namespace Seeing.Agent.Abstractions.Extensions;

/// <summary>
/// 提供 Hook 处理器的扩展
/// </summary>
public interface IHookExtension
{
    /// <summary>提供的 Hook 处理器</summary>
    IEnumerable<IHookHandler> GetHookHandlers();
}