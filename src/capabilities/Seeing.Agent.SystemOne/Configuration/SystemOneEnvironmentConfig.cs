namespace Seeing.Agent.SystemOne.Configuration;

/// <summary>
/// SystemOne 环境变量配置：常量 + 静态读取（仿 BootOverrideSource）。
/// <para>空白值一律视为未设置（返回 <c>null</c>）。</para>
/// </summary>
public static class SystemOneEnvironmentConfig
{
    /// <summary>API Key 环境变量名。</summary>
    public const string ApiKeyVar = "SYSTEMONE_API_KEY";

    /// <summary>Base URL 环境变量名。</summary>
    public const string BaseUrlVar = "SYSTEMONE_BASE_URL";

    /// <summary>模型环境变量名。</summary>
    public const string ModelVar = "SYSTEMONE_MODEL";

    /// <summary>默认 provider 环境变量名。</summary>
    public const string ProviderVar = "SYSTEMONE_PROVIDER";

    /// <summary>解析 API Key；未设置或空白返回 <c>null</c>。</summary>
    public static string? ResolveApiKey() => Read(ApiKeyVar);

    /// <summary>解析 Base URL；未设置或空白返回 <c>null</c>。</summary>
    public static string? ResolveBaseUrl() => Read(BaseUrlVar);

    /// <summary>解析模型；未设置或空白返回 <c>null</c>。</summary>
    public static string? ResolveModel() => Read(ModelVar);

    /// <summary>解析默认 provider id；未设置或空白返回 <c>null</c>。</summary>
    public static string? ResolveProvider() => Read(ProviderVar);

    private static string? Read(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
