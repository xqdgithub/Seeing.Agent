using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Agent.Core.Llm;

/// <summary>
/// LLM 客户端装饰器注册表（镜像 ToolDecoratorRegistry）。
/// </summary>
public sealed class LlmClientDecoratorRegistry : ILlmClientDecoratorRegistry
{
    private readonly object _gate = new();
    private readonly List<ILlmClientDecorator> _decorators = new();

    /// <summary>
    /// 注册客户端装饰器（同一实例重复注册会被忽略）。
    /// </summary>
    public void Register(ILlmClientDecorator decorator)
    {
        ArgumentNullException.ThrowIfNull(decorator);
        lock (_gate)
        {
            if (!_decorators.Contains(decorator))
                _decorators.Add(decorator);
        }
    }

    /// <summary>
    /// 注销装饰器，返回是否成功移除。
    /// </summary>
    public bool Unregister(ILlmClientDecorator decorator)
    {
        ArgumentNullException.ThrowIfNull(decorator);
        lock (_gate)
            return _decorators.Remove(decorator);
    }

    /// <summary>
    /// 按 Order 升序将全部装饰器依次包裹到客户端外层，返回装饰后的客户端。
    /// </summary>
    public ILlmClient Apply(ILlmClient client, ProviderConfig config)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(config);

        ILlmClientDecorator[] snapshot;
        lock (_gate)
            snapshot = _decorators.OrderBy(d => d.Order).ToArray();

        var current = client;
        foreach (var decorator in snapshot)
            current = decorator.Wrap(current, config);
        return current;
    }
}
