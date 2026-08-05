using Microsoft.Extensions.Logging;

namespace Seeing.Agent.Llm.Clients;

/// <summary>
/// OpenAI List Models API 客户端（GET /v1/models）
/// </summary>
internal class OpenAiModelsClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;

    public OpenAiModelsClient(ProviderConfig config, ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _httpClient = OpenAiHttpHelper.CreateHttpClient(
            config ?? throw new ArgumentNullException(nameof(config)),
            logger);
    }

    /// <summary>
    /// 获取可用模型列表
    /// </summary>
    public async Task<List<ModelInfo>> ListModelsAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("调用 List Models API");
        var response = await OpenAiHttpHelper.GetAsync(_httpClient, "models", ct);
        response.EnsureSuccessStatusCode();
        // List Models 错误通常简单（401/403），EnsureSuccessStatusCode 已够用

        var json = await response.Content.ReadAsStringAsync(ct);
        var result = OpenAiHttpHelper.Deserialize<ListModelsResponse>(json, _logger);
        return result?.Data ?? new List<ModelInfo>();
    }
}
