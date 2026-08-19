using Seeing.Agent.Abstractions.Llm;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Seeing.Agent.Llm.Clients;

/// <summary>
/// OpenAI 兼容接口共用 HTTP + SSE 解析辅助
/// </summary>
internal static class OpenAiHttpHelper
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// 创建并配置独立 HttpClient（用于没有 DI 工厂的模型目录客户端）。
    /// </summary>
    public static HttpClient CreateHttpClient(ProviderConfig config, ILogger logger)
    {
        var client = LlmHttpClientFactory.Create(config);
        ConfigureHttpClient(client, config, logger);
        return client;
    }

    /// <summary>
    /// 在已有 HttpClient 上应用 OpenAI 请求配置，不替换其 handler。
    /// 这保证 DI 工厂创建的代理 handler 能够被 OpenAI 客户端复用。
    /// </summary>
    public static void ConfigureHttpClient(HttpClient client, ProviderConfig config, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        var baseUrl = (config.BaseUrl ?? "https://api.openai.com/v1").TrimEnd('/');
        client.BaseAddress = new Uri(baseUrl + "/");
        client.Timeout = TimeSpan.FromMilliseconds(config.Timeout > 0 ? config.Timeout : 300000);
        client.DefaultRequestHeaders.Clear();
        if (!HttpHeaderHelper.Contains(config.Headers, "Authorization") &&
            !string.IsNullOrEmpty(config.ApiKey))
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {config.ApiKey}");
        client.DefaultRequestHeaders.Add("Accept", "application/json");

        HttpHeaderHelper.Apply(client, config.Headers);

        logger.LogDebug("OpenAI HTTP 客户端: BaseAddress={BaseAddress}, UseProxy={UseProxy}",
            client.BaseAddress, config.UseProxy);
    }

    /// <summary>
    /// POST JSON body，返回 HttpResponseMessage
    /// </summary>
    public static async Task<HttpResponseMessage> PostJsonAsync(
        HttpClient httpClient, string path, object body, ILogger logger, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(body, JsonOpts);
        logger.LogDebug("OpenAI POST {Path}: body={Json}", path, json);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    /// <summary>
    /// GET 请求，返回 HttpResponseMessage
    /// </summary>
    public static async Task<HttpResponseMessage> GetAsync(
        HttpClient httpClient, string path, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    /// <summary>
    /// 检查 HTTP 响应状态，失败时读取 body 并抛出带详细信息的异常
    /// </summary>
    public static async Task EnsureSuccessAsync(HttpResponseMessage response, ILogger logger, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        string? errorBody = null;
        try
        {
            errorBody = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "读取错误响应 body 失败");
        }

        var msg = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {errorBody ?? "(无 body)"}";
        logger.LogError("OpenAI API 错误: {Message}", msg);
        throw new HttpRequestException(msg, null, response.StatusCode);
    }

    /// <summary>
    /// 读取 Chat Completions 风格的 SSE 流——纯 data: 行，每行一个 JSON chunk
    /// </summary>
    public static async IAsyncEnumerable<ChatCompletionChunk> ReadChatCompletionsSseAsync(
        Stream responseStream,
        ILogger logger,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(responseStream);

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                logger.LogDebug("SSE 跳过非 data 行: {Line}", line);
                continue;
            }

            var data = line[5..].TrimStart();
            if (data == "[DONE]")
                yield break;

            ChatCompletionChunk? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<ChatCompletionChunk>(data, JsonOpts);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "SSE chunk JSON 解析失败，原始数据: {Raw}", data);
                continue;
            }

            if (chunk != null)
                yield return chunk;
        }
    }

    /// <summary>
    /// 读取 Responses API 风格的 SSE 流——event: + data: 双行
    /// </summary>
    public static async IAsyncEnumerable<ResponsesStreamEvent> ReadResponsesSseAsync(
        Stream responseStream,
        ILogger logger,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(responseStream);

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (!line.StartsWith("event:", StringComparison.Ordinal))
            {
                logger.LogDebug("Responses SSE 跳过非 event 行: {Line}", line);
                continue;
            }

            var eventType = line[6..].Trim();

            line = await reader.ReadLineAsync(ct);
            if (line == null) break;
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            var data = line[5..].TrimStart();
            if (data == "[DONE]")
                continue;

            ResponsesStreamEvent? evt;
            try
            {
                evt = JsonSerializer.Deserialize<ResponsesStreamEvent>(data, JsonOpts);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Responses SSE 事件 JSON 解析失败: type={EventType}, 原始数据: {Raw}", eventType, data);
                continue;
            }

            if (evt != null)
                yield return evt;
        }
    }

    /// <summary>
    /// 反序列化 JSON 字符串，失败时记录日志
    /// </summary>
    public static T? Deserialize<T>(string json, ILogger? logger = null)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOpts);
        }
        catch (JsonException ex)
        {
            logger?.LogError(ex, "JSON 反序列化失败，目标类型={Type}，原始数据({Len}字符): {Json}",
                typeof(T).Name, json.Length, json.Length > 2000 ? json[..2000] + "..." : json);
            throw;
        }
    }
}
