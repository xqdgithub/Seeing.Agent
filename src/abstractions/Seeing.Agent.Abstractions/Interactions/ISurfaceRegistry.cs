namespace Seeing.Agent.Abstractions.Interactions;

/// <summary>
/// 可呈现性注册表：判定"是否存在可呈现指定会话的提供方"的唯一入口。
/// </summary>
public interface ISurfaceRegistry
{
    void Register(ISurfaceProvider provider);

    void Unregister(ISurfaceProvider provider);

    /// <summary>是否存在可呈现指定会话的提供方。</summary>
    bool CanSurface(string sessionId);

    /// <summary>提供方注销（身份移除）；用于在途收敛复核。注册不触发，且先于 <see cref="Changed"/>。</summary>
    event Action? ProviderUnregistered;

    /// <summary>提供方集合或可呈现集合内容变化；用于 UI/诊断，不触发在途收敛。</summary>
    event Action? Changed;
}
