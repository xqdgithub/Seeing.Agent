namespace Seeing.Agent.Abstractions.Modules;

/// <summary>
/// 模块绑定的可复活长驻服务 — Deactivate 后 <see cref="IsRunning"/> 为 false，再 Activate 时由生命周期管理器重启。
/// </summary>
public interface IModuleHostedService
{
    /// <summary>所属模块 id（与 <see cref="ISeeingModule.Id"/> 对齐）。</summary>
    string ModuleId { get; }

    /// <summary>后台循环 / 托管资源是否处于运行态。</summary>
    bool IsRunning { get; }

    /// <summary>启动；已运行时须幂等 no-op。</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>停止；未运行时须幂等 no-op。</summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}
