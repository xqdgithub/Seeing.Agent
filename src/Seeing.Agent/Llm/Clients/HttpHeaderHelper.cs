namespace Seeing.Agent.Llm.Clients;

/// <summary>
/// 应用 Provider 自定义请求头。自定义值覆盖同名内置请求头，
/// 例如可用自定义 Authorization 覆盖默认的 ApiKey Bearer 头。
/// </summary>
internal static class HttpHeaderHelper
{
    public static bool Contains(
        IReadOnlyDictionary<string, string>? headers,
        string name)
        => headers?.Keys.Any(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) == true;

    public static void Apply(
        HttpClient client,
        IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null)
            return;

        foreach (var (key, value) in headers)
        {
            // 先移除内置头或同名旧值，避免产生重复 Authorization 等请求头。
            client.DefaultRequestHeaders.Remove(key);
            client.DefaultRequestHeaders.TryAddWithoutValidation(key, value);
        }
    }
}
