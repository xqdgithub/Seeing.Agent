namespace Seeing.Agent.Core.Modules;

/// <summary>
/// 进程级模块热重载选项（在途轮次边界）。
/// </summary>
public sealed class ModuleReloadOptions
{
    /// <summary>
    /// 为 true 时：Deactivate 前取消全部在途执行并报错（强制切换）。
    /// 为 false（默认）：有在途执行时推迟 Deactivate 至空闲。
    /// </summary>
    public bool ForceCancelInFlight { get; set; }
}
