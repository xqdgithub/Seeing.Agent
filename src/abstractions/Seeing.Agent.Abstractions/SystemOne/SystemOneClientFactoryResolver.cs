namespace Seeing.Agent.Abstractions.SystemOne;

/// <summary>
/// 从已登记的 <see cref="ISystemOneClientFactory"/> 集合中按类型解析工厂。
/// </summary>
public static class SystemOneClientFactoryResolver
{
    /// <summary>
    /// 解析支持指定类型的首个工厂（同类型多工厂时 first wins）；未找到返回 <c>null</c>。
    /// </summary>
    public static ISystemOneClientFactory? Find(
        IEnumerable<ISystemOneClientFactory> factories,
        string type)
    {
        ArgumentNullException.ThrowIfNull(factories);
        if (string.IsNullOrWhiteSpace(type))
            return null;

        return factories.FirstOrDefault(f => f.SupportsType(type));
    }

    /// <summary>
    /// 解析支持指定类型的首个工厂；未找到时抛 <see cref="NotSupportedException"/>，
    /// 消息中列出当前已支持的类型。
    /// </summary>
    public static ISystemOneClientFactory Require(
        IEnumerable<ISystemOneClientFactory> factories,
        string type)
    {
        ArgumentNullException.ThrowIfNull(factories);
        if (string.IsNullOrWhiteSpace(type))
            throw new ArgumentException("SystemOne provider type is required.", nameof(type));

        var factory = Find(factories, type);
        if (factory is null)
        {
            var supported = string.Join(
                ", ",
                factories.SelectMany(f => f.SupportedTypes).Distinct(StringComparer.OrdinalIgnoreCase));
            throw new NotSupportedException(
                $"不支持的 SystemOne Provider 类型: {type}（已支持: {supported}）");
        }

        return factory;
    }
}
