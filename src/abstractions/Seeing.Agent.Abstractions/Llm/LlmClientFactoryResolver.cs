namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// 从已登记的 <see cref="ILlmClientFactory"/> 集合中按类型解析工厂。
/// </summary>
public static class LlmClientFactoryResolver
{
    /// <summary>
    /// 解析支持指定类型的首个工厂（同类型多工厂时 first wins）。
    /// </summary>
    public static ILlmClientFactory Require(
        IEnumerable<ILlmClientFactory> factories,
        string type)
    {
        ArgumentNullException.ThrowIfNull(factories);
        if (string.IsNullOrWhiteSpace(type))
            throw new ArgumentException("Provider type is required.", nameof(type));

        var factory = factories.FirstOrDefault(f => f.SupportsType(type));
        if (factory is null)
            throw new NotSupportedException($"不支持的 Provider 类型: {type}");

        return factory;
    }
}
