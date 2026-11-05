namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// LLM Provider 注册表：按 id 持有 Provider 实例，并记录来源与生命周期归属。
/// <para><b>来源是既有事实</b>，存进来、读出来，不在使用方推断（见 <see cref="ProviderOrigin"/>）。</para>
/// </summary>
public interface IProviderRegistry
{
    /// <summary>当前全部 Provider 实例快照（不可变）。</summary>
    IReadOnlyDictionary<string, ILlmProvider> GetProviders();

    /// <summary>全部注册记录快照（含来源与生命周期）。</summary>
    IReadOnlyDictionary<string, ProviderRegistration> GetRegistrations();

    /// <summary>按 id 取 Provider；未注册返回 <c>null</c>。</summary>
    ILlmProvider? GetProvider(string id);

    /// <summary>按 id 取注册记录；未注册返回 <c>null</c>。</summary>
    ProviderRegistration? GetRegistration(string id);

    /// <summary>按 id 取来源；未注册返回 <c>null</c>。</summary>
    ProviderOrigin? GetOrigin(string id);

    /// <summary>按 id 取归属标识（扩展 id / 模块 id）；配置驱动或未注册返回 <c>null</c>。</summary>
    string? GetOwnerId(string id);

    /// <summary>是否由用户配置驱动（<see cref="ProviderOrigin.Configured"/>）。</summary>
    bool IsConfigured(string id);

    /// <summary>
    /// 注册 Provider。同 id 后注册者覆盖；是否释放被替换的实例由各自的
    /// <see cref="ProviderRegistration.DisposeByRegistry"/> 决定。
    /// </summary>
    void Register(ProviderRegistration registration);

    /// <summary>注销指定 id 并（按生命周期归属）释放实例，返回是否成功移除。</summary>
    bool Unregister(string id);

    /// <summary>注销指定归属的全部 Provider，返回移除数量。</summary>
    int UnregisterByOwner(string ownerId);

    /// <summary>注册集合变更事件（注册 / 注销后触发，携带最新快照与变更明细）。</summary>
    event EventHandler<ProvidersChangedEventArgs>? ProvidersChanged;
}
