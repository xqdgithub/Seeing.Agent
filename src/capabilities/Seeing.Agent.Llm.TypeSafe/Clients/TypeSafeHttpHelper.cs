using Seeing.Agent.Abstractions.Llm;
using System.Text;
using System.Text.Json;

namespace Seeing.Agent.Llm.TypeSafe.Clients;

/// <summary>
/// TypeSafe（Decision）HTTP 请求辅助：统一鉴权头、JSON 序列化、错误码映射与出站头管道。
/// <para><b>不含重试/退避</b>（spec §6.3 / §7.7）：429/529 退避由框架装饰器链按
/// <see cref="ProviderConfig"/> 的重试参数统一施加，客户端自带退避会造成双重退避。</para>
/// </summary>
internal static class TypeSafeHttpHelper
{
    private static JsonSerializerOptions JsonOpts => TypeSafeWireMap.JsonOptions;

    /// <summary>POST JSON 请求并反序列化响应。</summary>
    public static async Task<TResp> PostAsync<TReq, TResp>(
        HttpClient http,
        ProviderConfig config,
        string path,
        TReq body,
        LlmCallContext? call,
        CancellationToken ct,
        IReadOnlyList<ILlmCallInterceptor>? interceptors = null)
    {
        var json = JsonSerializer.Serialize(body, JsonOpts);
        return await SendAsync<TResp>(http, config, path, HttpMethod.Post, json, call, ct, interceptors)
            .ConfigureAwait(false);
    }

    /// <summary>GET 请求并反序列化响应。</summary>
    public static async Task<TResp> GetAsync<TResp>(
        HttpClient http,
        ProviderConfig config,
        string path,
        LlmCallContext? call,
        CancellationToken ct,
        IReadOnlyList<ILlmCallInterceptor>? interceptors = null)
        => await SendAsync<TResp>(http, config, path, HttpMethod.Get, jsonBody: null, call, ct, interceptors)
            .ConfigureAwait(false);

    private static async Task<TResp> SendAsync<TResp>(
        HttpClient http,
        ProviderConfig config,
        string path,
        HttpMethod method,
        string? jsonBody,
        LlmCallContext? call,
        CancellationToken ct,
        IReadOnlyList<ILlmCallInterceptor>? interceptors = null)
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
            ApplyHeaders(request, config, call, interceptors);

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
                        ?? throw new DecisionProtocolException(statusCode, responseBody, "TypeSafe 响应反序列化为空");
                }
                catch (JsonException ex)
                {
                    throw new DecisionProtocolException(
                        statusCode,
                        responseBody,
                        $"TypeSafe 响应反序列化失败: {ex.Message}",
                        ex);
                }
            }

            // 非 2xx：直接映射为带状态码与响应体的异常；是否重试由上层装饰器决定
            throw new DecisionProtocolException(
                statusCode,
                responseBody,
                $"TypeSafe 请求失败: HTTP {statusCode} {response.ReasonPhrase}");
        }
        catch (DecisionProtocolException)
        {
            throw;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            throw new DecisionProtocolException(0, null, $"TypeSafe 网络请求失败: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 出站头：内置 Bearer/Accept → 框架 <see cref="OutboundPipeline"/>
    /// （Provider 静态头 + <see cref="LlmCallContext.ExtraHeaders"/> + 调用拦截器，同名覆盖内置）。
    /// </summary>
    private static void ApplyHeaders(
        HttpRequestMessage request,
        ProviderConfig config,
        LlmCallContext? call,
        IReadOnlyList<ILlmCallInterceptor>? interceptors)
    {
        if (!ContainsHeader(config.Headers, "Authorization") && !string.IsNullOrEmpty(config.ApiKey))
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {config.ApiKey}");

        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        OutboundPipeline.Apply(
            request,
            staticHeaders: config.Headers,
            call,
            interceptors ?? Array.Empty<ILlmCallInterceptor>(),
            config.Id,
            config.Type);
    }

    private static bool ContainsHeader(IReadOnlyDictionary<string, string>? headers, string name)
        => headers?.Keys.Any(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) == true;
}
