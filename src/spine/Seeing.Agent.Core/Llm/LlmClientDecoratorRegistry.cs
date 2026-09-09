using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Agent.Core.Llm;

/// <summary>
/// LLM 客户端装饰器注册表（镜像 ToolDecoratorRegistry）。
/// </summary>
public sealed class LlmClientDecoratorRegistry : ILlmClientDecoratorRegistry
{
    private readonly object _gate = new();
    private readonly List<ILlmClientDecorator> _decorators = new();

    public void Register(ILlmClientDecorator decorator)
    {
        ArgumentNullException.ThrowIfNull(decorator);
        lock (_gate)
        {
            if (!_decorators.Contains(decorator))
                _decorators.Add(decorator);
        }
    }

    public bool Unregister(ILlmClientDecorator decorator)
    {
        ArgumentNullException.ThrowIfNull(decorator);
        lock (_gate)
            return _decorators.Remove(decorator);
    }

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
