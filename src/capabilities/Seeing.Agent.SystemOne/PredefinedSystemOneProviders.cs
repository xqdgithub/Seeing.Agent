using Seeing.Agent.Abstractions.SystemOne;

namespace Seeing.Agent.SystemOne;

/// <summary>内置 SystemOne provider 的默认配置工厂（纯数据，无副作用）。</summary>
public static class PredefinedSystemOneProviders
{
    /// <summary>内置 typesafe provider 的默认配置（未含环境变量覆盖）。</summary>
    public static SystemOneProviderConfig CreateDefaultTypeSafe() => new()
    {
        Id = "typesafe",
        Type = SystemOneProviderTypes.TypeSafe,
        Name = "TypeSafe Jev",
        BaseUrl = SystemOneDefaults.DefaultBaseUrl,
        Model = SystemOneDefaults.DefaultModel,
        Timeout = SystemOneDefaults.DefaultTimeoutMs,
        MaxRetries = 2
    };
}
