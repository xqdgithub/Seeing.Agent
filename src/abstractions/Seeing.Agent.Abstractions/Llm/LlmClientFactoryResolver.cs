namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// 从已登记的 <see cref="ILlmClientFactory"/> 集合中按协议解析工厂。
/// </summary>
public static class LlmClientFactoryResolver
{
    /// <summary>
    /// 解析支持指定协议的首个工厂（同协议多工厂时 first wins）。
    /// </summary>
    public static ILlmClientFactory Require(
        IEnumerable<ILlmClientFactory> factories,
        string protocol)
    {
        ArgumentNullException.ThrowIfNull(factories);
        if (string.IsNullOrWhiteSpace(protocol))
            throw new ArgumentException("Provider protocol is required.", nameof(protocol));

        var factory = factories.FirstOrDefault(f => f.SupportsProtocol(protocol));
        if (factory is null)
            throw new NotSupportedException($"不支持的 Provider 协议: {protocol}");

        return factory;
    }
}
