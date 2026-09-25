namespace Seeing.Agent.Mcp.OAuth;

/// <summary>
/// OAuth 客户端密钥（ClientSecret）的环境变量引用解析。
/// <para>
/// 支持的引用写法：<c>env:VAR_NAME</c> 与 <c>${VAR_NAME}</c>。
/// 配置文件中只写入引用（而非明文），由消费侧（令牌端点请求）在运行时解析为环境变量值，
/// 从而避免客户端密钥明文落盘。非引用值按明文原样返回（由用户自行承担文件权限风险）。
/// </para>
/// </summary>
public static class McpOAuthSecretResolver
{
    private const string EnvPrefix = "env:";

    /// <summary>判断给定值是否为环境变量引用（<c>env:VAR</c> 或 <c>${VAR}</c>）。</summary>
    public static bool IsEnvironmentReference(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;

        if (value.StartsWith(EnvPrefix, StringComparison.OrdinalIgnoreCase))
            return value.Length > EnvPrefix.Length;

        return value.Length > 3
            && value.StartsWith("${", StringComparison.Ordinal)
            && value.EndsWith('}');
    }

    /// <summary>
    /// 解析环境变量引用为实际密钥值。
    /// <para>引用未设置对应环境变量时返回 <c>null</c>（不回落到字面量，避免误用占位串当密钥）。</para>
    /// </summary>
    public static string? Resolve(string? value)
    {
        if (!IsEnvironmentReference(value))
            return value;

        var variableName = value!.StartsWith(EnvPrefix, StringComparison.OrdinalIgnoreCase)
            ? value[EnvPrefix.Length..]
            : value[2..^1];

        return Environment.GetEnvironmentVariable(variableName);
    }
}
