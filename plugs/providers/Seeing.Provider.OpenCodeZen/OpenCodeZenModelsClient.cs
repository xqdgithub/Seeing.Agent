using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Seeing.Provider.OpenCodeZen;

/// <summary>
/// OpenCode Zen 模型目录客户端：免认证拉取全量模型。
/// </summary>
public sealed class OpenCodeZenModelsClient
{
    /// <summary>OpenCode Zen 默认网关地址</summary>
    public const string DefaultBaseUrl = "https://opencode.ai/zen/v1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;
    private readonly ILogger _logger;

    public OpenCodeZenModelsClient(ILogger<OpenCodeZenModelsClient> logger)
        : this(new HttpClientHandler(), logger)
    {
    }

    public OpenCodeZenModelsClient(HttpMessageHandler handler, ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri(DefaultBaseUrl.TrimEnd('/') + "/")
        };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>
    /// 免认证拉取 OpenCode Zen 全量模型目录，并完成免费判定与能力预置。
    /// </summary>
    public async Task<IReadOnlyList<OpenCodeZenModel>> ListModelsAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync("models", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("OpenCode Zen List Models 失败: {StatusCode}", (int)response.StatusCode);
                return Array.Empty<OpenCodeZenModel>();
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var payload = JsonSerializer.Deserialize<ListModelsResponse>(json, JsonOptions);
            if (payload?.Data is null || payload.Data.Count == 0)
                return Array.Empty<OpenCodeZenModel>();

            return payload.Data
                .Where(m => !string.IsNullOrWhiteSpace(m.Id))
                .Select(m => CreateModel(m.Id!))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OpenCode Zen List Models 异常");
            return Array.Empty<OpenCodeZenModel>();
        }
    }

    private static OpenCodeZenModel CreateModel(string id)
    {
        var isFree = OpenCodeZenModelCatalog.IsFreeModel(id);
        var model = new OpenCodeZenModel
        {
            Id = id,
            Name = id,
            IsFree = isFree,
            InputPrice = isFree ? 0 : null,
            OutputPrice = isFree ? 0 : null
        };
        return OpenCodeZenModelCatalog.ApplyPreset(model);
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
