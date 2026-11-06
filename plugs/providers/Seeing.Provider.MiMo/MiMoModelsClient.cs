using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm.Support;
using Seeing.Agent.Llm.Support.Speech;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Seeing.Provider.MiMo;

public sealed class MiMoModelsClient
{
    public const string DefaultBaseUrl = "https://api.xiaomimimo.com/v1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;
    private readonly ILogger _logger;

    public MiMoModelsClient(ILogger<MiMoModelsClient> logger)
        : this(new HttpClientHandler(), logger)
    {
    }

    public MiMoModelsClient(HttpMessageHandler handler, ILogger logger)
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
                _logger.LogWarning("MiMo List Models 失败: {StatusCode}", (int)response.StatusCode);
                return Array.Empty<ModelConfig>();
            }

            var res = await response.Content.ReadAsStringAsync(ct);
            var payload = JsonSerializer.Deserialize<ListModelsResponse>(res, JsonOptions);
            if (payload?.Data is null || payload.Data.Count == 0)
                return Array.Empty<ModelConfig>();

            // 模型列表原样保留（含 asr/tts 等非对话条目）。
            // 远端目录不返回用途类型与模态，但 Router 两层都要它们：
            //   · 第 2 层要 types（缺省被当 Text，语音模型会被判「类型不含能力」）
            //   · 第 3 层要 modalities.input/output 含 audio（决议 24，不猜）
            var listed = payload.Data
                .Where(m => !string.IsNullOrWhiteSpace(m.Id))
                .Select(m => new ModelConfig
                {
                    Id = m.Id!,
                    Name = m.Id,
                    Provider = "mimo",
                    Types = ModelTypeInference.InferTypes(m.Id) ?? new List<ModelType>()
                })
                .Select(ModalityInference.Apply)
                .ToList();

            return listed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MiMo List Models 异常");
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
