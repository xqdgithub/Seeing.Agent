using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.SystemOne;
using System.Text;
using System.Text.Json;

namespace Seeing.Agent.Llm.SystemOne.Clients;

/// <summary>
/// TypeSafe（Decision）HTTP 请求辅助：统一鉴权头、JSON 序列化、错误码映射与出站头管道。
/// <para><b>不含重试/退避</b>（spec §6.3 / §7.7）：429/529 退避由框架装饰器链按
/// <see cref="ProviderConfig"/> 的重试参数统一施加，客户端自带退避会造成双重退避。</para>
/// </summary>
internal static class SystemOneHttpHelper
{
    private static JsonSerializerOptions JsonOpts => TypeSafeWireMap.JsonOptions;

    /// <summary>POST JSON 请求并反序列化响应。</summary>
    public static async Task<TResp> PostAsync<TReq, TResp>(
        HttpClient http,
        ProviderConfig config,
        string path,
        TReq body,
        LlmCallContext? call,
        CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(body, JsonOpts);
        return await SendAsync<TResp>(http, config, path, HttpMethod.Post, json, call, ct).ConfigureAwait(false);
    }

    /// <summary>GET 请求并反序列化响应。</summary>
    public static async Task<TResp> GetAsync<TResp>(
        HttpClient http,
        ProviderConfig config,
        string path,
        LlmCallContext? call,
        CancellationToken ct)
        => await SendAsync<TResp>(http, config, path, HttpMethod.Get, jsonBody: null, call, ct).ConfigureAwait(false);

    private static async Task<TResp> SendAsync<TResp>(
        HttpClient http,
        ProviderConfig config,
        string path,
        HttpMethod method,
        string? jsonBody,
        LlmCallContext? call,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(config);
        ct.ThrowIfCancellationRequested();

        var baseUrl = (config.BaseUrl ?? TypeSafeProtocol.DefaultBaseUrl).TrimEnd('/');
        var requestUri = baseUrl + path;

        try
        {
            using var request = new HttpRequestMessage(method, requestUri);
            if (jsonBody is not null)
                request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            ApplyHeaders(request, config, call);

            using var response = await http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            var statusCode = (int)response.StatusCode;
            var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                try
                {
                    return JsonSerializer.Deserialize<TResp>(responseBody, JsonOpts)
                        ?? throw new SystemOneException(statusCode, responseBody, "SystemOne 响应反序列化为空");
                }
                catch (JsonException ex)
                {
                    throw new SystemOneException(
                        statusCode,
                        responseBody,
                        $"SystemOne 响应反序列化失败: {ex.Message}",
                        ex);
                }
            }

            // 非 2xx：直接映射为带状态码与响应体的异常；是否重试由上层装饰器决定
            throw new SystemOneException(
                statusCode,
                responseBody,
                $"SystemOne 请求失败: HTTP {statusCode} {response.ReasonPhrase}");
        }
        catch (SystemOneException)
        {
            throw;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            throw new SystemOneException(0, null, $"SystemOne 网络请求失败: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 出站头：内置 Bearer/Accept → 框架 <see cref="OutboundPipeline"/>
    /// （Provider 静态头 + <see cref="LlmCallContext.ExtraHeaders"/>，同名覆盖内置）。
    /// </summary>
    private static void ApplyHeaders(HttpRequestMessage request, ProviderConfig config, LlmCallContext? call)
    {
        if (!ContainsHeader(config.Headers, "Authorization") && !string.IsNullOrEmpty(config.ApiKey))
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {config.ApiKey}");

        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        OutboundPipeline.Apply(
            request,
            staticHeaders: config.Headers,
            call,
            Array.Empty<ILlmCallInterceptor>(),
            config.Id,
            config.Type);
    }

    private static bool ContainsHeader(IReadOnlyDictionary<string, string>? headers, string name)
        => headers?.Keys.Any(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) == true;
}
