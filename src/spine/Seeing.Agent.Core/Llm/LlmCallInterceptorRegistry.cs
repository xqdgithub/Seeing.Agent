using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Agent.Core.Llm;

/// <summary>
/// 出站拦截器注册表。
/// </summary>
public sealed class LlmCallInterceptorRegistry : ILlmCallInterceptorRegistry
{
    private readonly object _gate = new();
    private readonly List<ILlmCallInterceptor> _interceptors = new();

    /// <summary>
    /// 注册出站拦截器（同一实例重复注册会被忽略）。
    /// </summary>
    public void Register(ILlmCallInterceptor interceptor)
    {
        ArgumentNullException.ThrowIfNull(interceptor);
        lock (_gate)
        {
            if (!_interceptors.Contains(interceptor))
                _interceptors.Add(interceptor);
        }
    }

    /// <summary>
    /// 注销拦截器，返回是否成功移除。
    /// </summary>
    public bool Unregister(ILlmCallInterceptor interceptor)
    {
        ArgumentNullException.ThrowIfNull(interceptor);
        lock (_gate)
            return _interceptors.Remove(interceptor);
    }

    /// <summary>
    /// 解析适用于指定 Provider 的拦截器链（按 Order 升序排列）。
    /// </summary>
    public IReadOnlyList<ILlmCallInterceptor> Resolve(string providerId, string providerType)
    {
        lock (_gate)
        {
            return _interceptors
                .Where(i => i.AppliesTo(providerId, providerType))
                .OrderBy(i => i.Order)
                .ToList();
        }
    }
}
