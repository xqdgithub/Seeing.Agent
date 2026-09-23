using Seeing.Agent.Abstractions.SystemOne;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Seeing.Agent.SystemOne.Clients;

/// <summary>
/// SystemOne HTTP 请求辅助：统一鉴权头、JSON 序列化、错误码处理与 429/529 退避重试。
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
        var maxRetries = config.MaxRetries > 0 ? config.MaxRetries : 0;

        for (var attempt = 0; ; attempt++)
        {
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

                if (!IsRetryableStatus(response.StatusCode) || attempt >= maxRetries)
                {
                    throw new SystemOneException(
                        statusCode,
                        responseBody,
                        $"SystemOne 请求失败: HTTP {statusCode} {response.ReasonPhrase}");
                }

                // 429/529：优先遵循 Retry-After，否则指数退避
                await Task.Delay(ComputeDelay(attempt, config, TryGetRetryAfter(response)), ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (IsRetryableNetworkError(ex, ct) && attempt < maxRetries)
            {
                await Task.Delay(ComputeDelay(attempt, config, retryAfter: null), ct).ConfigureAwait(false);
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

    private static bool IsRetryableStatus(HttpStatusCode status) => (int)status is 429 or 529;

    private static bool IsRetryableNetworkError(Exception ex, CancellationToken ct)
        => !ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException;

    private static TimeSpan ComputeDelay(int attempt, SystemOneProviderConfig config, TimeSpan? retryAfter)
    {
        var baseDelayMs = config.RetryBaseDelayMs > 0 ? config.RetryBaseDelayMs : 500;
        var maxDelayMs = config.RetryMaxDelayMs > 0 ? config.RetryMaxDelayMs : 5000;

        var exponent = Math.Min(attempt, 16);
        var delayMs = Math.Min(baseDelayMs * Math.Pow(2, exponent), maxDelayMs);
        var delay = TimeSpan.FromMilliseconds(delayMs);

        if (retryAfter is { } ra && ra > delay)
            delay = ra;

        return delay;
    }

    private static TimeSpan? TryGetRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null)
            return null;

        if (retryAfter.Delta is { } delta)
            return delta;

        if (retryAfter.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }
}
