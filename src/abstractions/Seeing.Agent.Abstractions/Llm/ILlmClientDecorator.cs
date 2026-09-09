namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// LLM 客户端装饰器（横切：重试等）。镜像工具装饰器模型。
/// </summary>
public interface ILlmClientDecorator
{
    int Order { get; }
    ILlmClient Wrap(ILlmClient inner, ProviderConfig config);
}

/// <summary>
/// 装饰器注册表：按 Order 包装客户端。
/// </summary>
public interface ILlmClientDecoratorRegistry
{
    void Register(ILlmClientDecorator decorator);
    bool Unregister(ILlmClientDecorator decorator);
    ILlmClient Apply(ILlmClient client, ProviderConfig config);
}
