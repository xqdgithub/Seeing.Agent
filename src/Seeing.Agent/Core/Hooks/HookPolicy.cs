namespace Seeing.Agent.Core.Hooks;

/// <summary>
/// Hook 执行策略
/// </summary>
public enum HookPolicy
{
    /// <summary>阻塞策略：等待所有 Handler 完成，支持 Mutable 修改</summary>
    Blocking,

    /// <summary>非阻塞策略：异步触发不等待，不支持 Mutable</summary>
    FireAndForget,

    /// <summary>并行策略：Handler 并行执行，不支持 Mutable</summary>
    Parallel
}