using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Agent.Core.Llm;

/// <summary>
/// 出站拦截器注册表。
/// </summary>
public sealed class LlmCallInterceptorRegistry : ILlmCallInterceptorRegistry
{
    private readonly object _gate = new();
    private readonly List<ILlmCallInterceptor> _interceptors = new();

    public void Register(ILlmCallInterceptor interceptor)
    {
        ArgumentNullException.ThrowIfNull(interceptor);
        lock (_gate)
        {
            if (!_interceptors.Contains(interceptor))
                _interceptors.Add(interceptor);
        }
    }

    public bool Unregister(ILlmCallInterceptor interceptor)
    {
        ArgumentNullException.ThrowIfNull(interceptor);
        lock (_gate)
            return _interceptors.Remove(interceptor);
    }

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
