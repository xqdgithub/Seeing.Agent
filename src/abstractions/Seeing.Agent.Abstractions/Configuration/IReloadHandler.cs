namespace Seeing.Agent.Abstractions.Configuration;

/// <summary>
/// 组件重载处理器：实现者必须声明订阅的变更类型（编译期可见、可自检）
/// <para>通过 DI 注册（AddSingleton&lt;IReloadHandler, T&gt;），由 ReloadOrchestrator 统一收集调度</para>
/// </summary>
public interface IReloadHandler
{
    /// <summary>组件标识（日志与诊断用）</summary>
    string ComponentId { get; }

    /// <summary>声明订阅的变更类型（支持订阅多个源）</summary>
    IReadOnlyList<Type> ChangeTypes { get; }

    /// <summary>执行重载（由编排器统一调度）</summary>
    Task ReloadAsync(IReloadSignal change, CancellationToken ct = default);
}

/// <summary>
/// 泛型便捷基类：Handler 直接拿到强类型变更数据，无需 pattern match
/// </summary>
public abstract class ReloadHandlerBase<TChange> : IReloadHandler where TChange : IReloadSignal
{
    /// <inheritdoc/>
    public abstract string ComponentId { get; }

    /// <inheritdoc/>
    public IReadOnlyList<Type> ChangeTypes { get; } = new[] { typeof(TChange) };

    /// <inheritdoc/>
    public Task ReloadAsync(IReloadSignal change, CancellationToken ct = default)
        => change is TChange typed ? ReloadAsync(typed, ct) : Task.CompletedTask;

    /// <summary>执行强类型重载</summary>
    protected abstract Task ReloadAsync(TChange change, CancellationToken ct);
}
