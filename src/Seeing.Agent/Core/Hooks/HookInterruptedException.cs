namespace Seeing.Agent.Core.Hooks;

/// <summary>
/// Hook 中断异常
/// </summary>
public sealed class HookInterruptedException : OperationCanceledException
{
    /// <summary>
    /// Hook 规格
    /// </summary>
    public HookSpec Spec { get; }

    /// <summary>
    /// Hook 结果
    /// </summary>
    public HookResult Result { get; }

    /// <summary>
    /// 创建 Hook 中断异常
    /// </summary>
    public HookInterruptedException(HookSpec spec, HookResult result)
        : base($"Hook {spec.Point} 中断执行")
    {
        Spec = spec;
        Result = result;
    }
}