namespace Seeing.Agent.Abstractions.Configuration;

/// <summary>重载执行结果（推送方与编排器共用）</summary>
public sealed class ReloadResult
{
    public string ComponentId { get; init; } = "";
    public bool Success { get; init; }
    public string? Error { get; init; }
    public TimeSpan Duration { get; init; }
}

/// <summary>
/// 重载信号总线：变更推送方（含插件/扩展包）通过此接口发布信号
/// <para>主库 ReloadOrchestrator 实现并注册为该接口；扩展包只引用 Abstractions，依赖规范合规</para>
/// </summary>
public interface IReloadSignalBus
{
    Task<IReadOnlyList<ReloadResult>> PublishAsync(IReloadSignal signal, CancellationToken ct = default);
}

/// <summary>
/// 重载处理器注册表：供运行时动态加载的插件（ExtensionManager 加载的 DLL）
/// 在 InitializeAsync 时注册/注销自己的 Handler；静态 DI 注册仍是推荐路径
/// </summary>
public interface IReloadHandlerRegistry
{
    void RegisterHandler(IReloadHandler handler);
    void UnregisterHandler(IReloadHandler handler);
}
