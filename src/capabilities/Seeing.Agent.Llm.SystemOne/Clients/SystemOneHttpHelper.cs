using Seeing.Agent.Abstractions.SystemOne;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Seeing.Agent.Llm.SystemOne.Clients;

/// <summary>
/// SystemOne HTTP 请求辅助：统一鉴权头、JSON 序列化与错误码映射。
/// <para><b>不含重试/退避</b>（spec §6.3 / §7.7）：429/529 退避由框架装饰器链
/// （<c>RetryLlmClientDecorator</c>）按 <see cref="ProviderConfig"/> 的重试参数统一施加，
/// 客户端自带退避会造成双重退避。</para>
/// </summary>
internal static class SystemOneHttpHelper
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>POST JSON 请求并反序列化响应。</summary>
    public static async Task<TResp> PostAsync<TReq, TResp>(
        HttpClient http,
        SystemOneProviderConfig config,
        string path,
        TReq body,
        CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(body, JsonOpts);
        return await SendAsync<TResp>(http, config, path, HttpMethod.Post, json, ct).ConfigureAwait(false);
    }

    /// <summary>GET 请求并反序列化响应。</summary>
    public static async Task<TResp> GetAsync<TResp>(
        HttpClient http,
        SystemOneProviderConfig config,
        string path,
        CancellationToken ct)
        => await SendAsync<TResp>(http, config, path, HttpMethod.Get, jsonBody: null, ct).ConfigureAwait(false);

    private static async Task<TResp> SendAsync<TResp>(
        HttpClient http,
        SystemOneProviderConfig config,
        string path,
        HttpMethod method,
        string? jsonBody,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(config);
        ct.ThrowIfCancellationRequested();

        var baseUrl = (config.BaseUrl ?? SystemOneDefaults.DefaultBaseUrl).TrimEnd('/');
        var requestUri = baseUrl + path;

        try
        {
            using var request = new HttpRequestMessage(method, requestUri);
            if (jsonBody is not null)
                request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            ApplyHeaders(request, config);

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

    private static void ApplyHeaders(HttpRequestMessage request, SystemOneProviderConfig config)
    {
        if (!string.IsNullOrEmpty(config.ApiKey))
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {config.ApiKey}");

        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        if (config.Headers is null)
            return;

        foreach (var (key, value) in config.Headers)
        {
            request.Headers.Remove(key);
            request.Headers.TryAddWithoutValidation(key, value);
        }
    }
}
