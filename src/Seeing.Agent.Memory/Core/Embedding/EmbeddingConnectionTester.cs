using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Llm;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Configuration;

namespace Seeing.Agent.Memory.Core.Embedding;

public sealed class EmbeddingConnectionTester : IEmbeddingConnectionTester
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<MemoryOptions> _options;
    private readonly IProviderEndpointLookup _providers;
    private readonly ILogger<EmbeddingConnectionTester>? _logger;

    public EmbeddingConnectionTester(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<MemoryOptions> options,
        IProviderEndpointLookup providers,
        ILogger<EmbeddingConnectionTester>? logger = null)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _providers = providers;
        _logger = logger;
    }

    public async Task<EmbeddingConnectionTestResult> TestAsync(
        string? provider = null,
        string? model = null,
        CancellationToken ct = default)
    {
        provider ??= _options.CurrentValue.Embedding.Provider;
        model ??= _options.CurrentValue.Embedding.Model;

        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model))
            return new EmbeddingConnectionTestResult(false, "请先填写 Embedding Provider 与 Model");

        if (!_providers.TryGet(provider, out var endpoint) || endpoint is null)
            return new EmbeddingConnectionTestResult(false, $"Providers 中未找到 '{provider}'");

        try
        {
            var baseUrl = (endpoint.BaseUrl ?? "https://api.openai.com/v1").TrimEnd('/');
            var client = _httpClientFactory.CreateClient(nameof(EmbeddingConnectionTester));
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/embeddings");
            if (!string.IsNullOrEmpty(endpoint.ApiKey))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);

            req.Content = JsonContent.Create(new { model, input = "seeing-memory-embedding-probe" });

            using var resp = await client.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                return new EmbeddingConnectionTestResult(false, $"HTTP {(int)resp.StatusCode}: {Truncate(body, 200)}");

            var parsed = System.Text.Json.JsonSerializer.Deserialize<ProbeResponse>(body);
            var dims = parsed?.Data?.FirstOrDefault()?.Embedding?.Length;
            if (dims is null or 0)
                return new EmbeddingConnectionTestResult(false, "响应成功但未返回向量");

            return new EmbeddingConnectionTestResult(true, $"连接成功，维度={dims}", dims);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Embedding connection test failed");
            return new EmbeddingConnectionTestResult(false, ex.Message);
        }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    private sealed class ProbeResponse
    {
        [JsonPropertyName("data")]
        public List<ProbeItem>? Data { get; set; }
    }

    private sealed class ProbeItem
    {
        [JsonPropertyName("embedding")]
        public float[]? Embedding { get; set; }
    }
}
