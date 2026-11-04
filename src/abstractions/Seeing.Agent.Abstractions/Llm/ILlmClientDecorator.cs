namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// LLM 客户端装饰器（横切：重试等）。镜像工具装饰器模型。
/// <para>类型从原先唯一的 chat 客户端接口泛化为 <see cref="ILlmCapabilityClient"/>（spec §7.7）：
/// 八个能力端口都要被覆盖，装饰器只在工厂内部应用。</para>
/// </summary>
public interface ILlmClientDecorator
{
    /// <summary>包装顺序；注册序靠内者先包（后注册居外层）。</summary>
    int Order { get; }

    /// <summary>包装客户端。实现须按能力的不同方法逐个委托，流式方法不得在产出过 chunk 后重试。</summary>
    ILlmCapabilityClient Wrap(ILlmCapabilityClient inner, ProviderConfig config);
}

/// <summary>
/// 装饰器注册表：按 Order 包装客户端。
/// </summary>
public interface ILlmClientDecoratorRegistry
{
    void Register(ILlmClientDecorator decorator);

    bool Unregister(ILlmClientDecorator decorator);

    /// <summary>依次应用全部装饰器，返回最外层客户端。</summary>
    ILlmCapabilityClient Apply(ILlmCapabilityClient client, ProviderConfig config);
}
