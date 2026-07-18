using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Llm;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Configuration;
using Seeing.Agent.Memory.Core.Models;

namespace Seeing.Agent.Memory.Core.Embedding;

public sealed class ProviderEmbeddingService : IEmbeddingService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<MemoryOptions> _options;
    private readonly IProviderEndpointLookup _providers;
    private readonly ILogger<ProviderEmbeddingService>? _logger;
    private int _dimensions;

    public ProviderEmbeddingService(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<MemoryOptions> options,
        IProviderEndpointLookup providers,
        ILogger<ProviderEmbeddingService>? logger = null)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _providers = providers;
        _logger = logger;
        _dimensions = options.CurrentValue.Embedding.Dimensions ?? 1536;
    }

    public int Dimensions => _dimensions;
    public string ProviderName => _options.CurrentValue.Embedding.Provider ?? "unconfigured";

    public async Task<EmbeddingResult> EmbedAsync(string text, CancellationToken ct = default)
    {
        var batch = await EmbedBatchAsync(new[] { text }, ct);
        return batch[0];
    }

    public async Task<IReadOnlyList<EmbeddingResult>> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken ct = default)
    {
        var mem = _options.CurrentValue;
        if (!mem.IsEmbeddingConfigured)
            throw new InvalidOperationException("Embedding is not configured");

        var providerName = mem.Embedding.Provider!;
        if (!_providers.TryGet(providerName, out var provider) || provider is null)
            throw new InvalidOperationException($"Provider '{providerName}' not found");

        var baseUrl = (provider.BaseUrl ?? "https://api.openai.com/v1").TrimEnd('/');
        var client = _httpClientFactory.CreateClient(nameof(ProviderEmbeddingService));
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/embeddings");
        if (!string.IsNullOrEmpty(provider.ApiKey))
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", provider.ApiKey);

        req.Content = JsonContent.Create(new
        {
            model = mem.Embedding.Model,
            input = texts.ToArray()
        });

        using var resp = await client.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var payload = await resp.Content.ReadFromJsonAsync<EmbeddingApiResponse>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Empty embedding response");

        var results = new List<EmbeddingResult>();
        foreach (var item in payload.Data.OrderBy(d => d.Index))
        {
            _dimensions = item.Embedding.Length;
            results.Add(new EmbeddingResult(texts[item.Index], item.Embedding, payload.Usage?.TotalTokens / texts.Count ?? 0));
        }
        return results;
    }

    private sealed class EmbeddingApiResponse
    {
        [JsonPropertyName("data")]
        public List<EmbeddingData> Data { get; set; } = new();
        [JsonPropertyName("usage")]
        public EmbeddingUsage? Usage { get; set; }
    }

    private sealed class EmbeddingData
    {
        [JsonPropertyName("index")]
        public int Index { get; set; }
        [JsonPropertyName("embedding")]
        public float[] Embedding { get; set; } = Array.Empty<float>();
    }

    private sealed class EmbeddingUsage
    {
        [JsonPropertyName("total_tokens")]
        public int TotalTokens { get; set; }
    }
}
