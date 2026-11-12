using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Seeing.Agent.Llm.Anthropic.Clients;

/// <summary>
/// Anthropic List Models API 客户端（GET {baseURL}/models）。
/// <para>不拥有 <see cref="HttpClient"/>——由 <see cref="AnthropicClient"/> 共享同一传输。</para>
/// </summary>
internal sealed class AnthropicModelsClient
{
    private const int PageLimit = 1000;

    private readonly ProviderConfig _config;
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;

    public AnthropicModelsClient(ProviderConfig config, HttpClient httpClient, ILogger logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// 拉取远端可用模型并映射为目录口径。失败时返回空集，不抛穿。
    /// </summary>
    public async Task<IReadOnlyList<ModelConfig>> ListModelsAsync(CancellationToken ct = default)
    {
        try
        {
            _logger.LogDebug("调用 Anthropic List Models API: Provider={ProviderId}", _config.Id);

            var listed = new List<ModelConfig>();
            string? afterId = null;

            while (true)
            {
                var path = afterId is null
                    ? $"models?limit={PageLimit}"
                    : $"models?limit={PageLimit}&after_id={Uri.EscapeDataString(afterId)}";

                using var response = await _httpClient
                    .SendAsync(new HttpRequestMessage(HttpMethod.Get, path), ct)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "Anthropic List Models 失败: Provider={ProviderId} Status={StatusCode}",
                        _config.Id,
                        (int)response.StatusCode);
                    return listed.Count > 0 ? listed : Array.Empty<ModelConfig>();
                }

                var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var page = JsonSerializer.Deserialize<ListModelsResponse>(json, SeeingJson.Readable);
                if (page?.Data is null || page.Data.Count == 0)
                    break;

                foreach (var entry in page.Data)
                {
                    if (string.IsNullOrWhiteSpace(entry.Id))
                        continue;
                    if (string.Equals(entry.Lifecycle, "retired", StringComparison.OrdinalIgnoreCase))
                        continue;

                    listed.Add(MapModel(entry));
                }

                if (!page.HasMore || string.IsNullOrWhiteSpace(page.LastId))
                    break;

                afterId = page.LastId;
            }

            return listed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Anthropic List Models 异常: Provider={ProviderId}", _config.Id);
            return Array.Empty<ModelConfig>();
        }
    }

    private ModelConfig MapModel(ModelInfo entry)
    {
        var model = new ModelConfig
        {
            Id = entry.Id!,
            Name = string.IsNullOrWhiteSpace(entry.DisplayName) ? entry.Id : entry.DisplayName,
            Provider = _config.Id
        };

        if (entry.MaxInputTokens is > 0)
            model.Limit.Context = entry.MaxInputTokens.Value;
        if (entry.MaxTokens is > 0)
            model.Limit.Output = entry.MaxTokens.Value;

        if (entry.Capabilities?.ImageInput?.Supported == true)
        {
            if (!model.Modalities.Input.Contains("image", StringComparer.OrdinalIgnoreCase))
                model.Modalities.Input.Add("image");
            if (!model.Modalities.Input.Contains("text", StringComparer.OrdinalIgnoreCase))
                model.Modalities.Input.Add("text");
            if (!model.Modalities.Output.Contains("text", StringComparer.OrdinalIgnoreCase))
                model.Modalities.Output.Add("text");
        }

        if (entry.Capabilities?.Thinking?.Supported == true)
        {
            model.Options = new ModelOptions
            {
                Thinking = new ThinkingOptions
                {
                    Supported = true,
                    Type = "enabled"
                }
            };
        }

        return model;
    }

    private sealed class ListModelsResponse
    {
        [JsonPropertyName("data")]
        public List<ModelInfo>? Data { get; set; }

        [JsonPropertyName("has_more")]
        public bool HasMore { get; set; }

        [JsonPropertyName("last_id")]
        public string? LastId { get; set; }
    }

    private sealed class ModelInfo
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("display_name")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("lifecycle")]
        public string? Lifecycle { get; set; }

        [JsonPropertyName("max_input_tokens")]
        public int? MaxInputTokens { get; set; }

        [JsonPropertyName("max_tokens")]
        public int? MaxTokens { get; set; }

        [JsonPropertyName("capabilities")]
        public WireCapabilities? Capabilities { get; set; }
    }

    private sealed class WireCapabilities
    {
        [JsonPropertyName("image_input")]
        public CapabilitySupport? ImageInput { get; set; }

        [JsonPropertyName("thinking")]
        public ThinkingCapability? Thinking { get; set; }
    }

    private sealed class CapabilitySupport
    {
        [JsonPropertyName("supported")]
        public bool Supported { get; set; }
    }

    private sealed class ThinkingCapability
    {
        [JsonPropertyName("supported")]
        public bool Supported { get; set; }
    }
}
