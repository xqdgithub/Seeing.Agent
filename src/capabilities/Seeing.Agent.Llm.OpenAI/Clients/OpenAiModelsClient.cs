using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm.Support;
using Seeing.Agent.Llm.Support.Speech;

namespace Seeing.Agent.Llm.OpenAI.Clients;

/// <summary>
/// OpenAI List Models API 客户端（GET {baseURL}/models）。
/// <para>不拥有 <see cref="HttpClient"/>——由 Chat/Responses 客户端共享同一传输。</para>
/// </summary>
internal sealed class OpenAiModelsClient
{
    private readonly ProviderConfig _config;
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;

    public OpenAiModelsClient(ProviderConfig config, HttpClient httpClient, ILogger logger)
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
            _logger.LogDebug("调用 List Models API: Provider={ProviderId}", _config.Id);
            using var response = await OpenAiHttpHelper.GetAsync(_httpClient, "models", ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "OpenAI List Models 失败: Provider={ProviderId} Status={StatusCode}",
                    _config.Id,
                    (int)response.StatusCode);
                return Array.Empty<ModelConfig>();
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var result = OpenAiHttpHelper.Deserialize<ListModelsResponse>(json, _logger);
            if (result?.Data is null || result.Data.Count == 0)
                return Array.Empty<ModelConfig>();

            var listed = new List<ModelConfig>();
            foreach (var entry in result.Data)
            {
                if (string.IsNullOrWhiteSpace(entry.Id))
                    continue;

                var model = new ModelConfig
                {
                    Id = entry.Id,
                    Name = entry.Id,
                    Provider = _config.Id,
                    Types = ModelTypeInference.InferTypes(entry.Id)
                };
                listed.Add(ModalityInference.Apply(model));
            }

            return listed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OpenAI List Models 异常: Provider={ProviderId}", _config.Id);
            return Array.Empty<ModelConfig>();
        }
    }
}
