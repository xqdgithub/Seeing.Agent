using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm.Support;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Seeing.Provider.SiliconFlow;

public sealed class SiliconFlowModelsClient
{
    public const string DefaultBaseUrl = "https://api.siliconflow.cn/v1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;
    private readonly ILogger _logger;

    public SiliconFlowModelsClient(ILogger<SiliconFlowModelsClient> logger)
        : this(new HttpClientHandler(), logger)
    {
    }

    public SiliconFlowModelsClient(HttpMessageHandler handler, ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri(DefaultBaseUrl.TrimEnd('/') + "/")
        };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<IReadOnlyList<ModelConfig>> ListModelsAsync(
        string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return Array.Empty<ModelConfig>();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "models");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("SiliconFlow List Models 失败: {StatusCode}", (int)response.StatusCode);
                return Array.Empty<ModelConfig>();
            }

            var res = await response.Content.ReadAsStringAsync(ct);
            var payload = JsonSerializer.Deserialize<ListModelsResponse>(res, JsonOptions);
            if (payload?.Data is null || payload.Data.Count == 0)
                return Array.Empty<ModelConfig>();

            var listed = payload.Data
                .Where(m => !string.IsNullOrWhiteSpace(m.Id))
                .Select(m => new ModelConfig
                {
                    Id = m.Id!,
                    Name = m.Id,
                    Provider = "siliconflow",
                    Types = ModelTypeInference.InferTypes(m.Id) ?? new List<ModelType>()
                })
                .ToList();

            return listed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SiliconFlow List Models 异常");
            return Array.Empty<ModelConfig>();
        }
    }

    private sealed class ListModelsResponse
    {
        [JsonPropertyName("data")]
        public List<ModelInfo>? Data { get; set; }
    }

    private sealed class ModelInfo
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }
    }
}
