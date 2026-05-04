namespace Seeing.Agent.Core.Hooks;

/// <summary>
/// Hook 处理器接口（单点监听）
/// </summary>
public interface IHookHandler
{
    /// <summary>
    /// Hook 规格
    /// </summary>
    HookSpec Spec { get; }

    /// <summary>
    /// 优先级（越小越先执行）
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// 执行 Hook
    /// </summary>
    Task<HookResult> ExecuteAsync(HookPayload payload);
}

/// <summary>
/// Hook 处理器接口（多点监听）
/// </summary>
public interface IMultiHookHandler
{
    /// <summary>
    /// Hook 规格列表
    /// </summary>
    IReadOnlyList<HookSpec> Specs { get; }

    /// <summary>
    /// 优先级（越小越先执行）
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// 执行 Hook
    /// </summary>
    Task<HookResult> ExecuteAsync(HookPayload payload);
}