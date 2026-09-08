namespace Seeing.Agent.Abstractions.Modules;

/// <summary>
/// <see cref="IModuleHostedService.IsRunning"/> 的轻量门闩 — 防双启、支持 Deactivate 后复活。
/// </summary>
public sealed class ModuleHostedRunGate
{
    private int _running;

    /// <summary>是否处于已占用（启动中/运行中）状态。</summary>
    public bool IsRunning => Volatile.Read(ref _running) != 0;

    /// <summary>尝试进入运行态；已运行则返回 false。</summary>
    public bool TryBegin() => Interlocked.CompareExchange(ref _running, 1, 0) == 0;

    /// <summary>退出运行态。</summary>
    public void End() => Interlocked.Exchange(ref _running, 0);
}
